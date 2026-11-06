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
            CallOnCateringComplete: true,
            CallOnRefuelStarted: true);

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
        var (state, _) = GroundUpcallCore.Evaluate(GroundUpcallCore.UpcallState.Initial, Sample(refuel: GsxServiceStage.Active));

        var (_, taxiInCall) = GroundUpcallCore.Evaluate(
            state,
            Sample(phase: FlightPhase.TaxiIn, refuel: GsxServiceStage.Completed));
        Assert.Null(taxiInCall);

        var (_, preflightCall) = GroundUpcallCore.Evaluate(
            state,
            Sample(refuel: GsxServiceStage.Completed));
        Assert.Equal(GroundUpcallCore.UpcallKind.RefuelComplete, preflightCall);
    }

    // Issue #151 (EGLL 2026-11-01): the startup resync seeded Refueling Completed a few ticks
    // after the first "Waiting" read — history, not a truck leaving.
    [Theory]
    [InlineData(GsxServiceStage.Waiting)]
    [InlineData(GsxServiceStage.Held)]
    [InlineData(GsxServiceStage.Skipped)]
    public void CompletionOutOfANonRunningStage_IsNotAnEdge(GsxServiceStage before)
    {
        var (state, _) = GroundUpcallCore.Evaluate(GroundUpcallCore.UpcallState.Initial, Sample(refuel: before, catering: before));
        var (_, call) = GroundUpcallCore.Evaluate(state, Sample(refuel: GsxServiceStage.Completed, catering: GsxServiceStage.Completed));
        Assert.Null(call);
    }

    [Theory]
    [InlineData(GsxServiceStage.Called)]
    [InlineData(GsxServiceStage.Requested)]
    [InlineData(GsxServiceStage.Active)]
    public void CompletionOutOfARunningStage_IsTheEdge(GsxServiceStage before)
    {
        var (state, _) = GroundUpcallCore.Evaluate(GroundUpcallCore.UpcallState.Initial, Sample(refuel: before));
        var (_, call) = GroundUpcallCore.Evaluate(state, Sample(refuel: GsxServiceStage.Completed));
        Assert.Equal(GroundUpcallCore.UpcallKind.RefuelComplete, call);
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

    [Fact]
    public void RefuelSkippedForTankering_MutesTheCompletionCall_UntilTheNextCycle()
    {
        // 2026-09-28 EDDN: GSX animated the truck and reported Completed, the sync moved no
        // fuel, and the crew still announced "refueling complete, 9.6 tonnes".
        var (state, _) = GroundUpcallCore.Evaluate(GroundUpcallCore.UpcallState.Initial, Sample());
        state = GroundUpcallCore.OnRefuelSkippedForTankering(state);

        (state, var call) = GroundUpcallCore.Evaluate(state, Sample(refuel: GsxServiceStage.Completed));
        Assert.Null(call);

        // A genuine refuel in the next turnaround still announces (the truck runs, then leaves).
        state = GroundUpcallCore.OnFlightCycleReset(state);
        (state, _) = GroundUpcallCore.Evaluate(state, Sample(refuel: GsxServiceStage.Active));
        (_, call) = GroundUpcallCore.Evaluate(state, Sample(refuel: GsxServiceStage.Completed));
        Assert.Equal(GroundUpcallCore.UpcallKind.RefuelComplete, call);
    }

    // ---- the smaller real-life lines (owner request 2026-11-07, plan V3) ----

    [Fact]
    public void RefuelActive_OutOfAnObservedStage_CallsOnce_ThenLatches()
    {
        var (state, baseline) = GroundUpcallCore.Evaluate(GroundUpcallCore.UpcallState.Initial, Sample(refuel: GsxServiceStage.Requested));
        Assert.Null(baseline);

        var (started, call) = GroundUpcallCore.Evaluate(state, Sample(refuel: GsxServiceStage.Active));
        Assert.Equal(GroundUpcallCore.UpcallKind.RefuelStarted, call);

        var (_, again) = GroundUpcallCore.Evaluate(started, Sample(refuel: GsxServiceStage.Active));
        Assert.Null(again);

        // A second truck on the same cycle (Completed → Active) is not announced twice.
        var (completed, done) = GroundUpcallCore.Evaluate(started, Sample(refuel: GsxServiceStage.Completed));
        Assert.Equal(GroundUpcallCore.UpcallKind.RefuelComplete, done);
        var (_, topUp) = GroundUpcallCore.Evaluate(completed, Sample(refuel: GsxServiceStage.Active));
        Assert.Null(topUp);
    }

    [Fact]
    public void RefuelActive_OnTheFirstRead_IsABaseline_NeverAStart()
    {
        var (_, call) = GroundUpcallCore.Evaluate(GroundUpcallCore.UpcallState.Initial, Sample(refuel: GsxServiceStage.Active));
        Assert.Null(call);
    }

    [Fact]
    public void RefuelStarted_ResetsWithTheCycle_AndRespectsItsSwitch()
    {
        var (state, _) = GroundUpcallCore.Evaluate(GroundUpcallCore.UpcallState.Initial, Sample(refuel: GsxServiceStage.Requested));
        var (latched, _) = GroundUpcallCore.Evaluate(state, Sample(refuel: GsxServiceStage.Active));
        Assert.True(latched.RefuelStartedCalled);
        Assert.False(GroundUpcallCore.OnFlightCycleReset(latched).RefuelStartedCalled);

        var off = Sample(refuel: GsxServiceStage.Active) with { CallOnRefuelStarted = false };
        var (afterOff, call) = GroundUpcallCore.Evaluate(state, off);
        Assert.Null(call);
        Assert.False(afterOff.RefuelStartedCalled);
    }

    [Fact]
    public void DeiceCompleted_SpeaksOncePerCycle()
    {
        var (state, first) = GroundUpcallCore.OnDeiceCompleted(GroundUpcallCore.UpcallState.Initial);
        var (again, second) = GroundUpcallCore.OnDeiceCompleted(state);
        Assert.True(first);
        Assert.False(second);
        Assert.False(GroundUpcallCore.OnFlightCycleReset(again).DeiceCalled);
    }

    [Theory]
    [InlineData(1, "Type one")]
    [InlineData(4, "Type four")]
    [InlineData(0, "")]
    [InlineData(9, "")]
    public void FluidSpoken_NamesTheGsxEnum(int fluid, string spoken)
        => Assert.Equal(spoken, GroundUpcallCore.FluidSpoken(fluid));
}
