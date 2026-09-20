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
        bool CateringCalled)
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
        bool CallOnCateringComplete);

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
        };

    /// <summary>One 1 Hz evaluation: at most one call per tick, in fixed priority order
    /// (ground power → chocks → refuel → catering).</summary>
    internal static (UpcallState State, UpcallKind? Call) Evaluate(UpcallState state, UpcallSample sample)
    {
        var departureWindow = sample.Phase.IsAtGate();
        var groundWindow = departureWindow || sample.Phase is FlightPhase.TaxiIn or FlightPhase.Shutdown;

        // Baseline on first observation — never announce state recovered at startup.
        var groundPowerRose = state.LastGroundPower is false && sample.GroundPower is true;
        var chocksRose = state.LastChocks is false && sample.Chocks is true;
        var refuelCompleted = state.LastRefuelStage is not null and not GsxServiceStage.Completed
            && sample.RefuelStage == GsxServiceStage.Completed;
        var cateringCompleted = state.LastCateringStage is not null and not GsxServiceStage.Completed
            && sample.CateringStage == GsxServiceStage.Completed;

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
}
