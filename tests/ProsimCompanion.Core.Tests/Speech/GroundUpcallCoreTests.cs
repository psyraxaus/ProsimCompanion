using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Crew;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>
/// Table tests for the ground-crew upcall decisions (campaign #78 — the review's "clearest
/// missing pure core": four one-shot latches × four edge baselines × phase windows, all
/// previously unreachable behind a private 1 Hz Tick).
/// </summary>
public sealed class GroundUpcallCoreTests
{
    private static GroundUpcallCore.UpcallSample Sample(
        FlightPhase phase = FlightPhase.Preflight,
        bool? groundPower = false,
        bool? chocks = false,
        GsxServiceStage? refuel = GsxServiceStage.Waiting,
        GsxServiceStage? catering = GsxServiceStage.Waiting)
        => new(
            Phase: phase,
            GroundPower: groundPower,
            Chocks: chocks,
            RefuelStage: refuel,
            CateringStage: catering,
            CallOnGroundPower: true,
            CallOnChocks: true,
            CallOnRefuelComplete: true,
            CallOnCateringComplete: true);

    [Fact]
    public void RisingEdge_CallsOnce_ThenLatchesForTheCycle()
    {
        var (state, _) = GroundUpcallCore.Evaluate(GroundUpcallCore.UpcallState.Initial, Sample());

        (state, var call) = GroundUpcallCore.Evaluate(state, Sample(groundPower: true));
        Assert.Equal(GroundUpcallCore.UpcallKind.GroundPower, call);

        // Falling then rising again inside the same cycle: latched, no repeat.
        (state, _) = GroundUpcallCore.Evaluate(state, Sample());
        (state, call) = GroundUpcallCore.Evaluate(state, Sample(groundPower: true));
        Assert.Null(call);
    }

    [Fact]
    public void FirstObservation_IsABaseline_NeverACall()
    {
        // Mid-turnaround app restart: GPU already connected — announcing it would replay
        // history (the startup-resync rule).
        var (_, call) = GroundUpcallCore.Evaluate(
            GroundUpcallCore.UpcallState.Initial,
            Sample(groundPower: true));

        Assert.Null(call);
    }

    [Fact]
    public void NullReads_PreserveTheBaseline()
    {
        var (state, _) = GroundUpcallCore.Evaluate(GroundUpcallCore.UpcallState.Initial, Sample());

        (state, _) = GroundUpcallCore.Evaluate(state, Sample(groundPower: null));
        Assert.False(state.LastGroundPower is null);

        var (_, call) = GroundUpcallCore.Evaluate(state, Sample(groundPower: true));
        Assert.Equal(GroundUpcallCore.UpcallKind.GroundPower, call);
    }

    [Fact]
    public void RefuelComplete_FiresOnlyInTheDepartureWindow()
    {
        var (state, _) = GroundUpcallCore.Evaluate(GroundUpcallCore.UpcallState.Initial, Sample());

        var (_, taxiInCall) = GroundUpcallCore.Evaluate(
            state,
            Sample(phase: FlightPhase.TaxiIn, refuel: GsxServiceStage.Completed));
        Assert.Null(taxiInCall);

        var (_, preflightCall) = GroundUpcallCore.Evaluate(
            state,
            Sample(refuel: GsxServiceStage.Completed));
        Assert.Equal(GroundUpcallCore.UpcallKind.RefuelComplete, preflightCall);
    }

    [Fact]
    public void ChocksOnArrival_FireInTheWiderGroundWindow()
    {
        var (state, _) = GroundUpcallCore.Evaluate(
            GroundUpcallCore.UpcallState.Initial,
            Sample(phase: FlightPhase.TaxiIn));

        var (_, call) = GroundUpcallCore.Evaluate(
            state,
            Sample(phase: FlightPhase.Shutdown, chocks: true));

        Assert.Equal(GroundUpcallCore.UpcallKind.Chocks, call);
    }

    [Fact]
    public void OneCallPerTick_InPriorityOrder()
    {
        var (state, _) = GroundUpcallCore.Evaluate(GroundUpcallCore.UpcallState.Initial, Sample());

        var (next, call) = GroundUpcallCore.Evaluate(
            state,
            Sample(groundPower: true, chocks: true));

        Assert.Equal(GroundUpcallCore.UpcallKind.GroundPower, call);
        // The chocks edge was consumed into the baseline — it fires on ITS next rising edge,
        // not this one (matches the original else-if chain).
        Assert.True(next.LastChocks);
    }

    [Fact]
    public void CycleReset_ClearsLatches_ButKeepsBaselines()
    {
        var (state, _) = GroundUpcallCore.Evaluate(GroundUpcallCore.UpcallState.Initial, Sample());
        (state, _) = GroundUpcallCore.Evaluate(state, Sample(groundPower: true));
        Assert.True(state.GroundPowerCalled);

        state = GroundUpcallCore.OnFlightCycleReset(state);

        Assert.False(state.GroundPowerCalled);
        // "Still true" must not become a rising edge in the new cycle.
        Assert.True(state.LastGroundPower);
        var (_, call) = GroundUpcallCore.Evaluate(state, Sample(groundPower: true));
        Assert.Null(call);
    }

    [Fact]
    public void DisabledCalls_AreSkipped_WithoutConsumingTheLatch()
    {
        var (state, _) = GroundUpcallCore.Evaluate(GroundUpcallCore.UpcallState.Initial, Sample());

        var (next, call) = GroundUpcallCore.Evaluate(
            state,
            Sample(groundPower: true) with { CallOnGroundPower = false });

        Assert.Null(call);
        Assert.False(next.GroundPowerCalled);
    }
}
