using ProsimCompanion.Gsx.Sync;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

/// <summary>Table tests for the refuel sync's decision function (campaign #78).</summary>
public sealed class RefuelCoreTests
{
    private static RefuelCore.RefuelInputs Inputs(
        double currentKg = 3000,
        double fuelTargetRaw = 7000,
        bool hose = true,
        bool planAvailable = true)
        => new(
            RequireOfp: true,
            PlanAvailable: planAvailable,
            HoseConnected: hose,
            CurrentKg: currentKg,
            FuelTargetRaw: fuelTargetRaw,
            FuelTargetKgRaw: 0,
            PlannedFuelRaw: 0,
            FinishOnHose: false,
            AllowDefuel: false,
            SkipOnTankering: true,
            RefuelMethod: "fixed",
            FixedRateKgPerSec: 20,
            TimeTargetSeconds: 300);

    private static RefuelCore.RefuelState Active(double currentKg = 3000, double targetRaw = 7000)
        => RefuelCore.OnRefuelActive(RefuelCore.RefuelState.Idle, Inputs(currentKg, targetRaw)).State;

    [Fact]
    public void Activation_LatchesTheTarget_RoundedUpToHundred()
    {
        var outcome = RefuelCore.OnRefuelActive(RefuelCore.RefuelState.Idle, Inputs(fuelTargetRaw: 6923));

        Assert.True(outcome.State.TransferActive);
        Assert.Equal(7000, outcome.State.LatchedTargetKg);
        Assert.Contains(outcome.Decisions, d => d.StartsWith("activated"));
    }

    [Fact]
    public void Activation_WithoutAPlan_HoldsForArming()
    {
        // #60: GSX-side refuel requests bypass every app-side gate; never latch from
        // stale/absent plan data.
        var outcome = RefuelCore.OnRefuelActive(
            RefuelCore.RefuelState.Idle,
            Inputs(planAvailable: false));

        Assert.False(outcome.State.TransferActive);
        Assert.True(outcome.State.PendingPlanArm);
        Assert.Equal(0, outcome.State.LatchedTargetKg);
    }

    [Fact]
    public void DeferredArm_FiresTheMomentThePlanArrives()
    {
        var pending = RefuelCore.OnRefuelActive(
            RefuelCore.RefuelState.Idle,
            Inputs(planAvailable: false)).State;

        var stillWaiting = RefuelCore.Tick(pending, Inputs(planAvailable: false));
        Assert.False(stillWaiting.State.TransferActive);
        Assert.Empty(stillWaiting.Decisions);

        var armed = RefuelCore.Tick(pending, Inputs());
        Assert.True(armed.State.TransferActive);
        Assert.False(armed.State.PendingPlanArm);
        Assert.Contains(armed.Decisions, d => d.Contains("arming now"));
    }

    [Fact]
    public void TankeringSkip_WhenFobAlreadyMeetsThePlan()
    {
        var outcome = RefuelCore.OnRefuelActive(
            RefuelCore.RefuelState.Idle,
            Inputs(currentKg: 6990, fuelTargetRaw: 7000));

        Assert.False(outcome.State.TransferActive);
        Assert.Contains(outcome.Decisions, d => d.Contains("tankering"));
    }

    [Fact]
    public void Tick_StepsTowardTheTarget_AtTheFixedRate_WithPumpOn()
    {
        var outcome = RefuelCore.Tick(Active(), Inputs());

        Assert.Equal(3020, outcome.WriteFuelKg);
        Assert.True(outcome.RefuelPower);
        Assert.Contains(outcome.Decisions, d => d.Contains("pump on"));
        Assert.True(outcome.State.PumpPowerOn);
    }

    [Fact]
    public void Completion_SnapsToTolerance_AndDropsPower()
    {
        var state = Active() with { PumpPowerOn = true };

        var outcome = RefuelCore.Tick(state, Inputs(currentKg: 6990));

        Assert.Equal(7000, outcome.WriteFuelKg);
        Assert.False(outcome.RefuelPower);
        Assert.False(outcome.State.TransferActive);
        Assert.Contains(outcome.Decisions, d => d.Contains("target reached"));
    }

    [Fact]
    public void HosePulled_Pauses_AndReconnectResumes()
    {
        var state = Active() with { HoseWasConnected = true, PumpPowerOn = true };

        var paused = RefuelCore.Tick(state, Inputs(hose: false));
        Assert.Null(paused.WriteFuelKg);
        Assert.False(paused.RefuelPower);
        Assert.Contains(paused.Decisions, d => d.Contains("paused"));
        Assert.True(paused.State.TransferActive);

        var resumed = RefuelCore.Tick(paused.State, Inputs());
        Assert.NotNull(resumed.WriteFuelKg);
        Assert.Contains(resumed.Decisions, d => d.Contains("hose connected"));
    }

    [Fact]
    public void FinishOnHose_CompletesInstantlyAtTheLatchedTarget()
    {
        var state = Active() with { HoseWasConnected = true, PumpPowerOn = true };

        var outcome = RefuelCore.Tick(
            state,
            Inputs(hose: false) with { FinishOnHose = true });

        Assert.Equal(7000, outcome.WriteFuelKg);
        Assert.False(outcome.RefuelPower);
        Assert.False(outcome.State.TransferActive);
    }

    [Fact]
    public void DefuelGuard_HoldsWhenTheTargetIsBelowCurrent()
    {
        var outcome = RefuelCore.Tick(Active(), Inputs(currentKg: 8000));

        Assert.Null(outcome.WriteFuelKg);
        Assert.NotNull(outcome.Hold);
        Assert.Contains("defuel disabled", outcome.Hold);
        Assert.True(outcome.State.TransferActive); // holds, never cancels

        var allowed = RefuelCore.Tick(
            Active(),
            Inputs(currentKg: 8000) with { AllowDefuel = true });
        Assert.NotNull(allowed.WriteFuelKg);
        Assert.True(allowed.WriteFuelKg < 8000);
    }

    [Fact]
    public void LatchedTarget_IgnoresMidTransferRewrites_LoggedOnce()
    {
        // ProSim rewrites fuelTarget to the current FOB when its refuel session engages
        // (round-4 smoke test) — the latched figure rules.
        var state = Active();

        var first = RefuelCore.Tick(state, Inputs(fuelTargetRaw: 3100));
        Assert.Contains(first.Decisions, d => d.Contains("keeping latched 7000"));
        Assert.Equal(7000, first.State.LatchedTargetKg);

        var second = RefuelCore.Tick(first.State, Inputs(currentKg: 3020, fuelTargetRaw: 3100));
        Assert.DoesNotContain(second.Decisions, d => d.Contains("keeping latched"));
    }

    [Fact]
    public void LateTargetLatch_KeepsTryingUntilAFigureAppears()
    {
        var state = RefuelCore.OnRefuelActive(
            RefuelCore.RefuelState.Idle,
            Inputs(fuelTargetRaw: 0)).State;
        Assert.Equal(0, state.LatchedTargetKg);

        var waiting = RefuelCore.Tick(state, Inputs(fuelTargetRaw: 0));
        Assert.Equal("no fuel target available — waiting", waiting.Hold);

        var latched = RefuelCore.Tick(waiting.State, Inputs());
        Assert.Equal(7000, latched.State.LatchedTargetKg);
        Assert.Contains(latched.Decisions, d => d.Contains("latched target 7000"));
    }

    [Fact]
    public void DynamicRate_ComputedOnceFromTheFirstTick()
    {
        var state = Active(currentKg: 3000, targetRaw: 6000);
        var dynamicInputs = Inputs(currentKg: 3000, fuelTargetRaw: 6000) with
        {
            RefuelMethod = "dynamicRate",
            TimeTargetSeconds = 100,
        };

        var first = RefuelCore.Tick(state, dynamicInputs);
        Assert.Equal(30, first.State.DynamicRateKgPerSec); // 3000 kg over 100 s
        Assert.Contains(first.Decisions, d => d.Contains("dynamic rate"));
        Assert.Equal(3030, first.WriteFuelKg);

        var second = RefuelCore.Tick(
            first.State,
            dynamicInputs with { CurrentKg = 3030 });
        Assert.DoesNotContain(second.Decisions, d => d.Contains("dynamic rate"));
        Assert.Equal(3060, second.WriteFuelKg);
    }

    [Fact]
    public void GsxCompleted_ShortOfTarget_SnapsToTheLatchedFigure()
    {
        var state = Active() with { PumpPowerOn = true };

        var outcome = RefuelCore.OnRefuelCompleted(state, Inputs(currentKg: 5000));

        Assert.Equal(7000, outcome.WriteFuelKg);
        Assert.False(outcome.RefuelPower);
        Assert.False(outcome.State.TransferActive);
        Assert.Contains(outcome.Decisions, d => d.Contains("snapped"));
    }

    [Fact]
    public void GsxCompleted_WhileHoldingForAPlan_MovesNoFuel()
    {
        var pending = RefuelCore.OnRefuelActive(
            RefuelCore.RefuelState.Idle,
            Inputs(planAvailable: false)).State;

        var outcome = RefuelCore.OnRefuelCompleted(pending, Inputs());

        Assert.Null(outcome.WriteFuelKg);
        Assert.False(outcome.State.PendingPlanArm);
        Assert.Contains(outcome.Decisions, d => d.Contains("no fuel was moved"));
    }
}
