using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Speech.Crew;

/// <summary>The walkaround's place in the departure cycle (issue #196).</summary>
public enum WalkaroundState
{
    /// <summary>Nothing yet this cycle.</summary>
    Idle,

    /// <summary>The FO has announced the walk and is out of the cockpit.</summary>
    Departed,

    /// <summary>The report has been given; the FO is back.</summary>
    Reported,

    /// <summary>The walk was cut short (a refuel call, the aircraft no longer ready).</summary>
    Abandoned,

    /// <summary>Over for this cycle, whichever way it ended.</summary>
    Done,
}

/// <summary>What the shell must do after one evaluation.</summary>
public enum WalkaroundAction
{
    None,

    /// <summary>Speak "I'll do the walkaround" and mark the FO away.</summary>
    Announce,

    /// <summary>Deliver the report and mark the FO back.</summary>
    Report,

    /// <summary>Mark the FO back without a report.</summary>
    Abandon,
}

/// <summary>One tick's inputs, sampled by the shell.</summary>
/// <param name="Live">The flight-live gate.</param>
/// <param name="Parked">The committed parked fact.</param>
/// <param name="EnginesOff">The committed engines-off fact.</param>
/// <param name="PrepComplete">The GSX ground prep's stage reads Complete (jetway / stairs on).</param>
/// <param name="RefuelStage">The refuel row on the service board; a call abandons the walk.</param>
/// <param name="BoardingObserved">The boarding row on the service board reads Called, Requested,
/// Active or Completed — boarding has started (or is over, the seeded Completed of a resync
/// included): the FO does not go outside any more (owner report 2026-11-01; ADR-0019 §9 — the
/// walk is allowed when boarding has not started, restart or not).</param>
public sealed record WalkaroundSample(
    FlightPhase Phase,
    bool Live,
    bool Parked,
    bool EnginesOff,
    bool PrepComplete,
    GsxServiceStage? RefuelStage,
    DateTimeOffset NowUtc,
    bool BoardingObserved = false);

/// <summary>What a restart did to the walk (ADR-0019 §9, issue #220).</summary>
public enum WalkaroundRestartOutcome
{
    /// <summary>Nothing had happened this cycle: the walk is still possible on the ready edge.</summary>
    StillPossible,

    /// <summary>The FO was outside when the app went down: simply back, no report.</summary>
    WalkEnded,

    /// <summary>The walk had already happened (reported or abandoned): over for this cycle.</summary>
    AlreadyDone,
}

/// <summary>What the walkaround keeps across an app restart (the flight-state snapshot part).</summary>
public sealed record WalkaroundSnapshotState(WalkaroundState State, DateTimeOffset? DepartedAtUtc, DateTimeOffset? ReportDueUtc);

/// <summary>
/// The pure walkaround state machine (issue #196): arms on the EDGE into "at the gate, live,
/// parked, engines off, ground prep complete, boarding not yet complete" — baselined on first
/// observation like the upcalls — and runs once per departure cycle: announce, a random away
/// window, the report; or abandon when the refuel is called (by voice or Stream Deck) or the
/// aircraft stops being ready. The shell owns the clock, the speech, the stores and the
/// findings.
///
/// Restart (owner rule 2026-11-01, first 1.0.0-rc.1 leg; revised by ADR-0019 §9 on 2026-11-02,
/// issue #220): the baseline alone did not hold — at startup the phase and the GSX prep stage
/// arrive seconds apart, so the first ticks read "not ready" and the resync's seeded Complete
/// looked like an edge; the FO walked out of an aircraft whose boarding had finished. The
/// first fix skipped the whole cycle after any restart; the owner's rule is now "a restart is
/// invisible": a boarding that has started (any stage from Called on, the seeded Completed
/// included) is never ready, and a restored snapshot (<see cref="AdoptRestart"/>) only ends a
/// walk that was in progress or already done — an idle cycle stays armed for its edge.
/// </summary>
public sealed class WalkaroundCore
{
    private bool? _lastReady;

    public WalkaroundState State { get; private set; } = WalkaroundState.Idle;

    /// <summary>When the FO left; null unless walking.</summary>
    public DateTimeOffset? DepartedAtUtc { get; private set; }

    /// <summary>When the report is due; null unless walking.</summary>
    public DateTimeOffset? ReportDueUtc { get; private set; }

    /// <summary>A new departure cycle: the walk may happen again on the next ready edge.
    /// The baseline is dropped to "not ready" so a turnaround whose prep completes again arms.</summary>
    public void OnFlightCycleReset()
    {
        State = WalkaroundState.Idle;
        DepartedAtUtc = null;
        ReportDueUtc = null;
        _lastReady = false;
    }

    /// <summary>The ready predicate: at the gate, live, parked, engines off, prep complete,
    /// boarding not started.</summary>
    public static bool IsReady(WalkaroundSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        return sample.Live && sample.Phase.IsAtGate() && sample.Parked && sample.EnginesOff && sample.PrepComplete && !sample.BoardingObserved;
    }

    /// <summary>Boarding has started as far as the service board can tell: any stage from the
    /// trigger on, the seeded Completed of a startup resync included.</summary>
    public static bool BoardingStarted(GsxServiceStage? boardingStage)
        => boardingStage is GsxServiceStage.Called or GsxServiceStage.Requested or GsxServiceStage.Active or GsxServiceStage.Completed;

    /// <summary>The state worth keeping across a restart.</summary>
    public WalkaroundSnapshotState Capture() => new(State, DepartedAtUtc, ReportDueUtc);

    /// <summary>An app restart inside a departure cycle (ADR-0019 §9): a walk in progress is
    /// neither resumed nor reported — the FO is simply back; a walk already reported or
    /// abandoned stays done; an idle cycle stays armed, baselined "not ready" so the ready
    /// state it finds at startup counts as its edge (boarding started blocks it regardless).</summary>
    public WalkaroundRestartOutcome AdoptRestart(WalkaroundSnapshotState? saved)
    {
        var state = saved?.State ?? WalkaroundState.Idle;
        DepartedAtUtc = null;
        ReportDueUtc = null;
        switch (state)
        {
            case WalkaroundState.Idle:
                State = WalkaroundState.Idle;
                _lastReady = false;
                return WalkaroundRestartOutcome.StillPossible;

            case WalkaroundState.Departed:
                State = WalkaroundState.Done;
                _lastReady = null;
                return WalkaroundRestartOutcome.WalkEnded;

            default:
                State = WalkaroundState.Done;
                _lastReady = null;
                return WalkaroundRestartOutcome.AlreadyDone;
        }
    }

    /// <summary>The away window: minimum plus the 0–1 roll's share of the spread, never
    /// below one minute; a maximum below the minimum reads as the minimum.</summary>
    public static TimeSpan AwayWindow(WalkaroundOptions options, double roll)
    {
        ArgumentNullException.ThrowIfNull(options);
        var min = Math.Max(1, options.MinMinutes);
        var max = Math.Max(min, options.MaxMinutes);
        return TimeSpan.FromMinutes(min + Math.Clamp(roll, 0, 1) * (max - min));
    }

    /// <summary>One evaluation. The first sample only baselines (a state that already holds
    /// at startup is never announced).</summary>
    public WalkaroundAction Evaluate(WalkaroundSample sample, WalkaroundOptions options, Func<double> roll)
    {
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(roll);

        var ready = IsReady(sample);
        var edge = _lastReady == false && ready;
        _lastReady = ready;

        switch (State)
        {
            case WalkaroundState.Idle:
                if (edge && options.Enabled)
                {
                    State = WalkaroundState.Departed;
                    DepartedAtUtc = sample.NowUtc;
                    ReportDueUtc = sample.NowUtc + AwayWindow(options, roll());
                    return WalkaroundAction.Announce;
                }

                return WalkaroundAction.None;

            case WalkaroundState.Departed:
                if (sample.RefuelStage is GsxServiceStage.Called or GsxServiceStage.Requested or GsxServiceStage.Active
                    || !ready)
                {
                    State = WalkaroundState.Abandoned;
                    return WalkaroundAction.Abandon;
                }

                if (ReportDueUtc is { } due && sample.NowUtc >= due)
                {
                    State = WalkaroundState.Reported;
                    return WalkaroundAction.Report;
                }

                return WalkaroundAction.None;

            case WalkaroundState.Reported:
            case WalkaroundState.Abandoned:
                State = WalkaroundState.Done;
                return WalkaroundAction.None;

            default:
                return WalkaroundAction.None;
        }
    }

    /// <summary>How long the FO was away, for the report event.</summary>
    public TimeSpan? Duration(DateTimeOffset nowUtc) => DepartedAtUtc is { } since ? nowUtc - since : null;
}
