using ProsimCompanion.Gsx.Sync;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

/// <summary>Table tests for the boarding tick's decision layer (campaign #78).</summary>
public sealed class BoardingCoreTests
{
    private static BoardingCore.TickInputs Boarding(
        int boarded = 10,
        int lastWritten = 5,
        double cargo = 0,
        double lastCargo = 0)
        => new(
            BoardingActive: true,
            PendingPlanArm: false,
            DeboardingActive: false,
            Enabled: true,
            DeboardEnabled: true,
            PlanAvailable: true,
            BoardedCounter: boarded,
            LastWrittenBoarded: lastWritten,
            CargoPercent: cargo,
            LastWrittenCargoPct: lastCargo,
            DeboardCounter: 0,
            LastWrittenDeboard: -1,
            HaveDeboardMap: false,
            DeboardStartCount: 0,
            DeboardCargoPercent: 0,
            LastWrittenDeboardCargoPct: -1);

    [Fact]
    public void BoardedCounterChange_TriggersASeatSync()
    {
        Assert.True(BoardingCore.PlanTick(Boarding(boarded: 10, lastWritten: 5)).SyncBoardedSeats);
        Assert.False(BoardingCore.PlanTick(Boarding(boarded: 10, lastWritten: 10)).SyncBoardedSeats);
        Assert.False(BoardingCore.PlanTick(Boarding(boarded: -1, lastWritten: 5)).SyncBoardedSeats);
    }

    [Fact]
    public void CargoSyncs_OnlyOnMovesOfAtLeastOnePercent()
    {
        Assert.True(BoardingCore.PlanTick(Boarding(cargo: 51, lastCargo: 50)).SyncBoardingCargo);
        Assert.False(BoardingCore.PlanTick(Boarding(cargo: 50.5, lastCargo: 50)).SyncBoardingCargo);
    }

    [Fact]
    public void DeferredPlanArm_FiresOnlyWhenThePlanArrives_AndSyncsTheSameTick()
    {
        var pending = Boarding() with { BoardingActive = false, PendingPlanArm = true, PlanAvailable = false };
        var held = BoardingCore.PlanTick(pending);
        Assert.False(held.ArmBoardingNow);
        Assert.False(held.SyncBoardedSeats);

        var armed = BoardingCore.PlanTick(pending with { PlanAvailable = true });
        Assert.True(armed.ArmBoardingNow);
        // Catch-up (#60): the same tick seats everyone GSX already boarded.
        Assert.True(armed.SyncBoardedSeats);
    }

    [Fact]
    public void Deboarding_DrainsFrontFirst_AgainstTheUpCountingTotal()
    {
        var inputs = Boarding() with
        {
            BoardingActive = false,
            DeboardingActive = true,
            HaveDeboardMap = true,
            DeboardStartCount = 120,
            DeboardCounter = 30,
            LastWrittenDeboard = 20,
        };

        var plan = BoardingCore.PlanTick(inputs);

        Assert.True(plan.SyncDeboardedSeats);
        Assert.Equal(90, plan.DeboardTargetRemaining);
    }

    [Fact]
    public void DeboardCargo_InvertsTheUnloadPercent()
    {
        var inputs = Boarding() with
        {
            BoardingActive = false,
            DeboardingActive = true,
            DeboardCargoPercent = 40,
            LastWrittenDeboardCargoPct = 20,
        };

        var plan = BoardingCore.PlanTick(inputs);

        Assert.True(plan.SyncDeboardCargo);
        Assert.Equal(60, plan.DeboardRemainingCargoPercent);
    }

    [Fact]
    public void DisabledSync_DoesNothing()
    {
        Assert.False(BoardingCore.PlanTick(Boarding() with { Enabled = false }).SyncBoardedSeats);

        var deboard = Boarding() with
        {
            BoardingActive = false,
            DeboardingActive = true,
            HaveDeboardMap = true,
            DeboardCounter = 5,
            DeboardEnabled = false,
        };
        Assert.False(BoardingCore.PlanTick(deboard).SyncDeboardedSeats);
    }

    [Fact]
    public void PlanGateOnArming_HoldsExactlyWhenOfpRequiredAndAbsent()
    {
        Assert.True(BoardingCore.ShouldHoldForPlan(requireOfp: true, planAvailable: false));
        Assert.False(BoardingCore.ShouldHoldForPlan(requireOfp: true, planAvailable: true));
        Assert.False(BoardingCore.ShouldHoldForPlan(requireOfp: false, planAvailable: false));
    }
}
