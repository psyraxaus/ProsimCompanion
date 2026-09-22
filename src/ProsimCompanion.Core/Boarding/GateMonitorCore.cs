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
public sealed record GateMonitorInputs(
    GsxServiceStage? BoardingStage,
    int? PaxTarget,
    int? PaxBoarded,
    bool? Door1LOpen,
    bool BoardingBridgeConnected,
    FlightPhase Phase,
    DateTimeOffset? StdUtc,
    DateTimeOffset NowUtc,
    bool GsxConnected);

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

    /// <summary>The current latched state.</summary>
    public GateState State => _state;

    /// <summary>Back to CLOSED for a new turnaround (GSX flight-cycle reset, or a fresh
    /// Preflight after the previous leg closed).</summary>
    public void Reset(DateTimeOffset now)
    {
        _state = GateState.Closed;
        _changedAt = now;
        _closedDetail = null;
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

        var paxFlowing = inputs.PaxBoarded is > 0;
        var completed = inputs.BoardingStage == GsxServiceStage.Completed;
        var pastGate = !inputs.Phase.IsBeforeTaxiOut() && inputs.Phase != FlightPhase.Unknown;

        // A closed-after-boarding leg that has reached a new Preflight with no boarding
        // evidence is the next turnaround: reset without waiting for the explicit signal.
        if (_state == GateState.ClosedAfterBoarding
            && inputs.Phase.IsAtGate()
            && inputs.BoardingStage is null or GsxServiceStage.Waiting or GsxServiceStage.Held
            && !paxFlowing)
        {
            Reset(inputs.NowUtc);
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
