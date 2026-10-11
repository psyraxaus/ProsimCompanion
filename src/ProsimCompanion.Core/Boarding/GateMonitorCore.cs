using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Boarding;

/// <summary>The evidence one gate-monitor evaluation reads. Nulls mean "not known" (GSX or
/// ProSim absent) and never move the state on their own.</summary>
/// <param name="BoardingStage">The LATCHED GSX Boarding stage (never the raw mirror — GSX
/// flips a finished service back to "available").</param>
/// <param name="PaxTarget">GSX planned passengers.</param>
/// <param name="PaxBoarded">GSX boarded so far.</param>
/// <param name="Door1LOpen">ProSim door 1L (forward left entry).</param>
/// <param name="BoardingBridgeConnected">Jetway or stairs operate service Active/Completed.</param>
/// <param name="Phase">The committed flight phase.</param>
/// <param name="StdUtc">Effective scheduled departure, if known.</param>
/// <param name="NowUtc">The (sim) clock.</param>
/// <param name="GsxConnected">False renders "GSX offline" instead of "Not called".</param>
/// <param name="OfpRequestId">The current OFP's request id, or null without a plan. A new id
/// is a new leg (issue #173): the latch resets so the second leg gets its own chain.</param>
public sealed record GateMonitorInputs(
    GsxServiceStage? BoardingStage,
    int? PaxTarget,
    int? PaxBoarded,
    bool? Door1LOpen,
    bool BoardingBridgeConnected,
    FlightPhase Phase,
    DateTimeOffset? StdUtc,
    DateTimeOffset NowUtc,
    bool GsxConnected,
    string? OfpRequestId = null);

/// <summary>
/// Pure gate-monitor state machine (owner spec 2026-09-22). States only move forward
/// 1 → 2 → 3 → 4 → 5; the only way back is <see cref="Reset"/> (new flight cycle) — so a
/// GSX counter glitch or a door cycling briefly never makes the card flap. Skips are allowed
/// forward: pax flowing before a "called" edge jumps straight to BOARDING; a completed
/// boarding that never met the final-call thresholds jumps to closed. Timer-driven callers
/// use <see cref="Evaluate"/> each tick; it is cheap and returns the same snapshot (by value)
/// when nothing changed, so the store notifies nobody.
/// </summary>
public sealed class GateMonitorCore
{
    private GateState _state = GateState.Closed;
    private DateTimeOffset? _changedAt;
    private string? _closedDetail;
    private string? _ofpRequestId;

    /// <summary>The current latched state.</summary>
    public GateState State => _state;

    /// <summary>Back to CLOSED for a new turnaround (GSX flight-cycle reset, a new OFP, or a
    /// fresh Preflight after the previous leg closed). The leg's OFP id is forgotten with it.</summary>
    public void Reset(DateTimeOffset now)
    {
        _state = GateState.Closed;
        _changedAt = now;
        _closedDetail = null;
        _ofpRequestId = null;
    }

    /// <summary>Applies one evaluation. Thresholds come from the options (web-editable);
    /// <paramref name="finalCallPaxPercent"/> ≥ 100 and <paramref name="finalCallMinutesBeforeStd"/>
    /// ≤ 0 each disable their trigger.</summary>
    public GateStatusSnapshot Evaluate(
        GateMonitorInputs inputs,
        string? gateId,
        int finalCallPaxPercent,
        int finalCallMinutesBeforeStd)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        // A new OFP is a new leg (issue #173): a latch from the previous plan — including a
        // ClosedAfterBoarding restored from last leg's counters after an app restart — never
        // carries into it. The id is remembered per leg and forgotten on every reset.
        if (inputs.OfpRequestId is { Length: > 0 } ofp)
        {
            if (_ofpRequestId is not null && !string.Equals(_ofpRequestId, ofp, StringComparison.Ordinal))
            {
                Reset(inputs.NowUtc);
            }

            _ofpRequestId = ofp;
        }

        // GSX's boarded counter is per boarding session and keeps last leg's total until the
        // next call (EGCC 2026-10-11: 122/122 all through the turnaround). It moves the state
        // FORWARD only while GSX itself says a boarding is called, running or done; the raw
        // count still keeps the Preflight auto-reset below conservative within a leg.
        var paxReported = inputs.PaxBoarded is > 0;
        var paxFlowing = paxReported
            && inputs.BoardingStage is GsxServiceStage.Called or GsxServiceStage.Requested
                or GsxServiceStage.Active or GsxServiceStage.Completed;
        var completed = inputs.BoardingStage == GsxServiceStage.Completed;
        // Taxi-out through taxi-in: the gate is behind us. Shutdown is NOT past the gate for
        // this purpose — the arrival's cycle reset lands there, and a Closed strip must not
        // re-close itself before the next leg's boarding is even called (issue #173).
        var pastGate = !inputs.Phase.IsBeforeTaxiOut()
            && inputs.Phase is not (FlightPhase.Unknown or FlightPhase.Shutdown);

        // A closed-after-boarding leg that has reached a new Preflight with no boarding
        // evidence is the next turnaround: reset without waiting for the explicit signal.
        if (_state == GateState.ClosedAfterBoarding
            && inputs.Phase.IsAtGate()
            && inputs.BoardingStage is null or GsxServiceStage.Waiting or GsxServiceStage.Held
            && !paxReported)
        {
            Reset(inputs.NowUtc);
            _ofpRequestId = inputs.OfpRequestId is { Length: > 0 } ? inputs.OfpRequestId : null;
        }

        // Settle fully in one evaluation: an app started mid-boarding at 95 % must read
        // FINAL CALL now, not BOARDING for a tick and FINAL CALL on the next.
        var next = _state;
        for (var step = 0; step < 4; step++)
        {
            var candidate = Step(next, inputs, completed, pastGate, paxFlowing, finalCallPaxPercent, finalCallMinutesBeforeStd);
            if (candidate == next)
            {
                break;
            }

            next = candidate;
        }

        // FINAL CALL may be skipped (3 → 5) but never re-entered; BOARDING → FINAL CALL and
        // the closing edge can both be true on one tick, and closing wins inside Step.
        if (next != _state)
        {
            _state = next;
            _changedAt = inputs.NowUtc;
            if (next == GateState.ClosedAfterBoarding)
            {
                _closedDetail = inputs.Door1LOpen == false || completed
                    ? $"Doors closed {inputs.NowUtc:HH:mm}Z"
                    : "Boarding complete";
            }
        }

        int? minutesToStd = inputs.StdUtc is { } std
            ? (int)Math.Round((std - inputs.NowUtc).TotalMinutes)
            : null;

        var detail = _state switch
        {
            GateState.Closed => inputs.GsxConnected ? "Not called" : "GSX offline",
            GateState.Open => inputs.BoardingBridgeConnected ? "Jetway · door 1L" : "Boarding called",
            GateState.Boarding => "Boarding",
            GateState.FinalCall => "Final call",
            _ => _closedDetail ?? "Boarding complete",
        };

        return new GateStatusSnapshot(
            _state,
            gateId,
            inputs.PaxBoarded,
            inputs.PaxTarget,
            inputs.StdUtc,
            minutesToStd,
            detail,
            _changedAt,
            Dimmed: pastGate);
    }

    /// <summary>One forward edge from <paramref name="state"/>, or the same state.</summary>
    private static GateState Step(
        GateState state, GateMonitorInputs inputs, bool completed, bool pastGate, bool paxFlowing,
        int finalCallPaxPercent, int finalCallMinutesBeforeStd)
    {
        var next = state;
        switch (state)
        {
            case GateState.Closed:
                if (completed || pastGate)
                {
                    next = GateState.ClosedAfterBoarding;
                }
                else if (paxFlowing || inputs.BoardingStage == GsxServiceStage.Active)
                {
                    next = GateState.Boarding;
                }
                else if (inputs.BoardingStage is GsxServiceStage.Called or GsxServiceStage.Requested
                    || (inputs.BoardingBridgeConnected && inputs.Door1LOpen == true))
                {
                    next = GateState.Open;
                }
                break;

            case GateState.Open:
                if (completed || pastGate)
                {
                    next = GateState.ClosedAfterBoarding;
                }
                else if (paxFlowing || inputs.BoardingStage == GsxServiceStage.Active)
                {
                    next = GateState.Boarding;
                }
                break;

            case GateState.Boarding:
                if (completed || pastGate || DoorClosedAfterBoarding(inputs))
                {
                    next = GateState.ClosedAfterBoarding;
                }
                else if (FinalCallDue(inputs, finalCallPaxPercent, finalCallMinutesBeforeStd))
                {
                    next = GateState.FinalCall;
                }
                break;

            case GateState.FinalCall:
                if (completed || pastGate || DoorClosedAfterBoarding(inputs))
                {
                    next = GateState.ClosedAfterBoarding;
                }
                break;
        }

        return next;
    }

    private static bool FinalCallDue(GateMonitorInputs inputs, int paxPercent, int minutesBeforeStd)
    {
        if (paxPercent < 100 && inputs.PaxTarget is > 0 && inputs.PaxBoarded is { } boarded
            && boarded * 100.0 / inputs.PaxTarget.Value >= paxPercent)
        {
            return true;
        }

        return minutesBeforeStd > 0 && inputs.StdUtc is { } std
            && std - inputs.NowUtc <= TimeSpan.FromMinutes(minutesBeforeStd);
    }

    /// <summary>Door 1L closed with everybody on board — the cabin crew shut the door before
    /// GSX reported Completed. Requires the full count so a door swing mid-boarding is ignored.</summary>
    private static bool DoorClosedAfterBoarding(GateMonitorInputs inputs)
        => inputs.Door1LOpen == false
           && inputs.PaxTarget is > 0
           && inputs.PaxBoarded is { } boarded
           && boarded >= inputs.PaxTarget.Value;
}
