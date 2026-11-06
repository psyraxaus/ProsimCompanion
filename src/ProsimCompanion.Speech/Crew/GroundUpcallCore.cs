using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Speech.Crew;

/// <summary>
/// The ground-crew upcall decision function, pure (campaign #78 — the mirror of
/// <see cref="Cabin.CabinCrewCore"/>, which this service never got): rising-edge detection
/// with first-observation baselining (a mid-turnaround app restart must never replay calls
/// for state that already held), the phase windows, the per-cycle one-shot latches, and the
/// one-call-per-tick priority order. The shell (<see cref="GroundCrewUpcallService"/>) owns
/// the MECH press, the INT latch wait and the arbiter enqueue.
/// </summary>
internal static class GroundUpcallCore
{
    internal enum UpcallKind
    {
        GroundPower,
        Chocks,
        RefuelComplete,
        CateringComplete,
        /// <summary>GSX Refueling went Active: "fuelling in progress, N tonnes requested" (owner request 2026-11-07, plan V3).</summary>
        RefuelStarted,
    }

    /// <summary>Edge baselines (null = not yet observed) + per-cycle called latches.</summary>
    internal readonly record struct UpcallState(
        bool? LastGroundPower,
        bool? LastChocks,
        GsxServiceStage? LastRefuelStage,
        GsxServiceStage? LastCateringStage,
        bool GroundPowerCalled,
        bool ChocksCalled,
        bool RefuelCalled,
        bool CateringCalled,
        bool RefuelStartedCalled,
        bool DeiceCalled)
    {
        internal static readonly UpcallState Initial = default;
    }

    internal sealed record UpcallSample(
        FlightPhase Phase,
        bool? GroundPower,
        bool? Chocks,
        GsxServiceStage? RefuelStage,
        GsxServiceStage? CateringStage,
        bool CallOnGroundPower,
        bool CallOnChocks,
        bool CallOnRefuelComplete,
        bool CallOnCateringComplete,
        bool CallOnRefuelStarted);

    /// <summary>A new ground-ops cycle: the called latches reset. Edge baselines survive
    /// deliberately — the equipment states themselves carry over into the new cycle, and a
    /// reset must not turn "still true" into a rising edge.</summary>
    internal static UpcallState OnFlightCycleReset(UpcallState state)
        => state with
        {
            GroundPowerCalled = false,
            ChocksCalled = false,
            RefuelCalled = false,
            CateringCalled = false,
            RefuelStartedCalled = false,
            DeiceCalled = false,
        };

    /// <summary>The refuel sync retired the transfer for tankering while GSX Refueling ran
    /// anyway: the Completed edge still arrives, but "refueling complete, N tonnes" would
    /// announce fuel that never moved (2026-09-28 EDDN). Latch the call for this cycle.</summary>
    internal static UpcallState OnRefuelSkippedForTankering(UpcallState state)
        => state with { RefuelCalled = true };

    /// <summary>A service the crew or the sequencer has called and that GSX has taken up —
    /// the only stages a genuine completion edge can come out of.</summary>
    internal static bool IsRunning(GsxServiceStage? stage)
        => stage is GsxServiceStage.Called or GsxServiceStage.Requested or GsxServiceStage.Active;

    /// <summary>One 1 Hz evaluation: at most one call per tick, in fixed priority order
    /// (ground power → chocks → refuel → catering).</summary>
    internal static (UpcallState State, UpcallKind? Call) Evaluate(UpcallState state, UpcallSample sample)
    {
        var departureWindow = sample.Phase.IsAtGate();
        var groundWindow = departureWindow || sample.Phase is FlightPhase.TaxiIn or FlightPhase.Shutdown;

        // Baseline on first observation — never announce state recovered at startup.
        var groundPowerRose = state.LastGroundPower is false && sample.GroundPower is true;
        var chocksRose = state.LastChocks is false && sample.Chocks is true;
        // A completion is an edge only out of a RUNNING stage (issue #151, 2026-11-01: after a
        // restart at the gate the first tick read Refueling "Waiting", the startup resync then
        // seeded it Completed, and "refueling complete, 5.0 tonnes" was announced for the
        // previous session's refuel). Waiting/Held → Completed is history, not a truck leaving.
        var refuelCompleted = IsRunning(state.LastRefuelStage) && sample.RefuelStage == GsxServiceStage.Completed;
        var cateringCompleted = IsRunning(state.LastCateringStage) && sample.CateringStage == GsxServiceStage.Completed;
        // The truck starting (owner request 2026-11-07, plan V3): Active out of any OBSERVED other
        // stage — a first read of Active after a restart is a baseline, not a start.
        var refuelStarted = state.LastRefuelStage is { } lastRefuel && lastRefuel != GsxServiceStage.Active
            && sample.RefuelStage == GsxServiceStage.Active;

        state = state with
        {
            LastGroundPower = sample.GroundPower ?? state.LastGroundPower,
            LastChocks = sample.Chocks ?? state.LastChocks,
            LastRefuelStage = sample.RefuelStage ?? state.LastRefuelStage,
            LastCateringStage = sample.CateringStage ?? state.LastCateringStage,
        };

        if (groundPowerRose && groundWindow && sample.CallOnGroundPower && !state.GroundPowerCalled)
        {
            return (state with { GroundPowerCalled = true }, UpcallKind.GroundPower);
        }
        if (chocksRose && groundWindow && sample.CallOnChocks && !state.ChocksCalled)
        {
            return (state with { ChocksCalled = true }, UpcallKind.Chocks);
        }
        if (refuelStarted && departureWindow && sample.CallOnRefuelStarted && !state.RefuelStartedCalled)
        {
            return (state with { RefuelStartedCalled = true }, UpcallKind.RefuelStarted);
        }
        if (refuelCompleted && departureWindow && sample.CallOnRefuelComplete && !state.RefuelCalled)
        {
            return (state with { RefuelCalled = true }, UpcallKind.RefuelComplete);
        }
        if (cateringCompleted && departureWindow && sample.CallOnCateringComplete && !state.CateringCalled)
        {
            return (state with { CateringCalled = true }, UpcallKind.CateringComplete);
        }

        return (state, null);
    }

    /// <summary>GSX de-icing completed (owner request 2026-11-07, plan V3): the crew reports it
    /// once per cycle — the signal is a completion edge already, the latch guards a repeat.</summary>
    internal static (UpcallState State, bool Speak) OnDeiceCompleted(UpcallState state)
        => state.DeiceCalled ? (state, false) : (state with { DeiceCalled = true }, true);

    /// <summary>The GSX fluid enum as the crew says it; empty when unknown (0) so the clause drops.</summary>
    internal static string FluidSpoken(int fluidType) => fluidType switch
    {
        1 => "Type one",
        2 => "Type two",
        3 => "Type three",
        4 => "Type four",
        _ => "",
    };
}
