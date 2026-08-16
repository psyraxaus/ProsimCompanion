using ProsimCompanion.Gsx.Automation;
using ProsimCompanion.Gsx.Sync;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

/// <summary>Table tests for the arrival tick's decision function (campaign #78).</summary>
public sealed class ArrivalCoreTests
{
    private static int FixedRoll(int lo, int hi) => lo + 2;

    private static ArrivalCore.ArrivalInputs Parked(GsxAutomationPhase phase = GsxAutomationPhase.Arrival)
        => new(
            Enabled: true,
            Phase: phase,
            SnapshotValid: true,
            OnGround: true,
            AnyEngineRunning: false,
            ParkBrakeSet: true,
            GroundSpeedKt: 0.0,
            BeaconOn: false,
            ArrivalStableSecondsOption: 3,
            AutoCallDeboard: true,
            DeboardAlreadyCalled: false,
            AutoGroundEquipment: true,
            ChockDelayMinSec: 5,
            ChockDelayMaxSec: 20);

    private static ArrivalCore.ArrivalState Run(
        ref ArrivalCore.ArrivalState state,
        ArrivalCore.ArrivalInputs inputs,
        out ArrivalCore.ArrivalTickOutcome outcome)
    {
        outcome = ArrivalCore.Tick(state, inputs, FixedRoll);
        state = outcome.State;
        return state;
    }

    [Fact]
    public void StablePark_RunsArrivalActions_AfterTheConfiguredSeconds()
    {
        var state = ArrivalCore.ArrivalState.Initial;

        Run(ref state, Parked(), out var t1);
        Run(ref state, Parked(), out var t2);
        Assert.False(t1.RunArrivalActions);
        Assert.False(t2.RunArrivalActions);

        Run(ref state, Parked(), out var t3);
        Assert.True(t3.RunArrivalActions);
        Assert.True(t3.TryCallDeboard);
        Assert.Equal(7, t3.ChockCountdownStarted); // lo 5 + fixed roll offset 2
        Assert.True(state.ArrivalHandled);
    }

    [Fact]
    public void Movement_ResetsTheStableCounter()
    {
        var state = ArrivalCore.ArrivalState.Initial;
        Run(ref state, Parked(), out _);
        Run(ref state, Parked(), out _);

        Run(ref state, Parked() with { GroundSpeedKt = 8.0 }, out _);
        Assert.Equal(0, state.StableSeconds);

        // Needs the full window again from scratch.
        Run(ref state, Parked(), out var t4);
        Assert.False(t4.RunArrivalActions);
    }

    [Fact]
    public void FreshArrivalSegment_ResetsLatches_IncludingTheDeboardLatch()
    {
        var state = ArrivalCore.ArrivalState.Initial with { WasInArrivalPhases = false };

        Run(ref state, Parked(GsxAutomationPhase.TaxiIn), out var first);

        Assert.True(first.ResetDeboardLatch);
        Assert.False(state.ArrivalHandled);
    }

    [Fact]
    public void PendingArrival_StraddlesTheQuickFlipToPreparation()
    {
        // The phase engine flips Shutdown -> Preparation fast once parked; an unhandled
        // arrival keeps processing in Preparation instead of falling through to FOB restore.
        var state = ArrivalCore.ArrivalState.Initial;
        Run(ref state, Parked(), out _); // enters arrival phases

        Run(ref state, Parked(GsxAutomationPhase.Preparation), out var straddle);

        Assert.False(straddle.TryRestoreFob);
        Assert.True(state.WasInArrivalPhases);
    }

    [Fact]
    public void HandledArrival_InPreparation_FallsThroughToFobRestore()
    {
        var state = ArrivalCore.ArrivalState.Initial;
        for (var i = 0; i < 3; i++)
        {
            Run(ref state, Parked(), out _);
        }
        Assert.True(state.ArrivalHandled);

        Run(ref state, Parked(GsxAutomationPhase.Preparation), out var afterHandled);

        Assert.True(afterHandled.TryRestoreFob);
        Assert.False(afterHandled.RunJetwayArrivalStep);
    }

    [Fact]
    public void ChockCountdown_CountsDownWhileStable_ThenPlaces()
    {
        var state = ArrivalCore.ArrivalState.Initial;
        for (var i = 0; i < 3; i++)
        {
            Run(ref state, Parked(), out _);
        }
        Assert.Equal(7, state.ChockCountdown);

        ArrivalCore.ArrivalTickOutcome last = null!;
        for (var i = 0; i < 6; i++)
        {
            Run(ref state, Parked(), out last);
            Assert.False(last.PlaceChocks);
        }

        Run(ref state, Parked(), out last);
        Assert.True(last.PlaceChocks);
        Assert.Equal(-1, state.ChockCountdown);
    }

    [Fact]
    public void ChockCountdown_AbortsOnMovement()
    {
        var state = ArrivalCore.ArrivalState.Initial;
        for (var i = 0; i < 3; i++)
        {
            Run(ref state, Parked(), out _);
        }

        Run(ref state, Parked() with { BeaconOn = true }, out var moved);

        Assert.True(moved.ChockAborted);
        Assert.False(moved.PlaceChocks);
        Assert.Equal(-1, state.ChockCountdown);
    }

    [Fact]
    public void HandledArrival_KeepsOfferingJetwayAndDeboard_UntilDeboardLatched()
    {
        var state = ArrivalCore.ArrivalState.Initial;
        for (var i = 0; i < 3; i++)
        {
            Run(ref state, Parked(), out _);
        }

        Run(ref state, Parked(), out var offering);
        Assert.True(offering.RunJetwayArrivalStep);
        Assert.True(offering.TryCallDeboard);

        Run(ref state, Parked() with { DeboardAlreadyCalled = true }, out var latched);
        Assert.True(latched.RunJetwayArrivalStep);
        Assert.False(latched.TryCallDeboard);
    }

    [Fact]
    public void Disabled_DoesNothing_AndPreservesState()
    {
        var state = ArrivalCore.ArrivalState.Initial with { StableSeconds = 2 };

        var outcome = ArrivalCore.Tick(state, Parked() with { Enabled = false }, FixedRoll);

        Assert.Equal(state, outcome.State);
        Assert.False(outcome.RunArrivalActions);
        Assert.False(outcome.TryRestoreFob);
    }
}
