using ProsimCompanion.Gsx.Automation;
using ProsimCompanion.Gsx.Sync;
using ProsimCompanion.Sim;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

/// <summary>
/// The steering release (2026-11-14, GSX Pro 4.0.23 on the owner's rig): GSX left
/// <c>L:FSDT_VAR_Frozen</c> at 1 after the tug had gone and ProSim kept the nose wheel steering
/// disconnected (NW STRG DISC, dead tiller). The rule clears the flag once it has stood stale
/// for the grace — and never during a push.
/// </summary>
public sealed class PushbackSteeringReleaseTests
{
    private const int Grace = PushbackTickCore.SteeringReleaseGraceTicks;

    private static PushbackTickCore.SteeringRelease Evaluate(
        bool option = true, bool onGround = true, double frozen = 1, double pin = 0, int vehicleState = 1, int staleTicks = 0, int releases = 0)
        => PushbackTickCore.EvaluateSteeringRelease(option, onGround, frozen, pin, vehicleState, staleTicks, releases);

    [Fact]
    public void StaleFlag_IsClearedOnlyAfterTheGrace()
    {
        var ticks = 0;
        for (var second = 1; second < Grace; second++)
        {
            var waiting = Evaluate(staleTicks: ticks);
            Assert.False(waiting.Release);
            Assert.Equal(second, waiting.StaleTicks);
            ticks = waiting.StaleTicks;
        }

        var due = Evaluate(staleTicks: ticks);
        Assert.True(due.Release);
        Assert.Equal(0, due.StaleTicks);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(14)]
    public void TugHoldingNothing_CountsAsStale(int vehicleState)
        => Assert.True(Evaluate(vehicleState: vehicleState, staleTicks: Grace - 1).Release);

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    public void APushInProgress_IsNeverTouched_AndTheCountStartsOver(int vehicleState)
    {
        var verdict = Evaluate(vehicleState: vehicleState, staleTicks: Grace - 1);

        Assert.False(verdict.Release);
        Assert.Equal(0, verdict.StaleTicks);
    }

    [Fact]
    public void PinInserted_Airborne_FlagClear_OrOptionOff_DoNothing()
    {
        Assert.Equal(new PushbackTickCore.SteeringRelease(0, false, false), Evaluate(pin: 1, staleTicks: Grace - 1));
        Assert.Equal(new PushbackTickCore.SteeringRelease(0, false, false), Evaluate(onGround: false, staleTicks: Grace - 1));
        Assert.Equal(new PushbackTickCore.SteeringRelease(0, false, false), Evaluate(frozen: 0, staleTicks: Grace - 1));
        Assert.Equal(new PushbackTickCore.SteeringRelease(0, false, false), Evaluate(option: false, staleTicks: Grace - 1));
    }

    [Fact]
    public void AGsxThatKeepsSettingTheFlag_IsLeftAloneAfterTheCap_SaidOnce()
    {
        var capped = Evaluate(staleTicks: Grace - 1, releases: PushbackTickCore.SteeringReleaseMaxPerSegment);
        Assert.False(capped.Release);
        Assert.True(capped.GiveUp);

        var later = Evaluate(staleTicks: capped.StaleTicks, releases: PushbackTickCore.SteeringReleaseMaxPerSegment);
        Assert.False(later.Release);
        Assert.False(later.GiveUp);
        Assert.Equal(capped.StaleTicks, later.StaleTicks);
    }

    // ---- planner: the rule's answer as effects ----

    private static PushbackTickReading Reading(double frozen, int vehicleState = 1, double pin = 0, bool option = true, bool automation = false)
        => new(
            Phase: GsxAutomationPhase.PushBack,
            AutomationEnabled: automation,
            GsxReady: false,
            BeaconSequenceEnabled: false,
            DepartureComplete: true,
            VehicleState: vehicleState,
            PushbackStatus: 0,
            BypassPin: pin,
            HookInputs: new PushbackTickCore.HookInputs(
                false, false, false, false, false, false, false, false, false, false, "never", false, false, false, "never"),
            SequenceInputs: new PushbackSequencer.Inputs(false, false, false, false, true, true),
            Delays: new PushbackSequencer.Delays(1, 5, 1, 5, 1, 5),
            ConfirmEngineStartOption: false,
            Engines: null,
            GsxFrozen: frozen,
            ReleaseSteeringOption: option,
            OnGround: true);

    private static PushbackLatchSnapshot Latches(int staleTicks, int vehicleState = 1)
        => PushbackLatchSnapshot.Initial with
        {
            LastResetPhase = GsxAutomationPhase.PushBack,
            LastVehicleState = vehicleState,
            LastPushbackStatus = 0,
            FrozenStaleTicks = staleTicks,
        };

    [Fact]
    public void Planner_CountsTheStaleSeconds_ThenLogsAndReleases_WithTheAutomationOff()
    {
        var counting = PushbackTickEffectPlanner.Plan(Reading(frozen: 1), Latches(staleTicks: 2), sequenceWasReset: false, sequenceTick: null);
        Assert.Equal([new SetFrozenStaleTicksEffect(3)], counting);

        var due = PushbackTickEffectPlanner.Plan(Reading(frozen: 1), Latches(staleTicks: Grace - 1), sequenceWasReset: false, sequenceTick: null);
        Assert.Collection(
            due,
            log => Assert.Contains("L:FSDT_VAR_Frozen = 1", Assert.IsType<PushbackDecisionEffect>(log).Reason, StringComparison.Ordinal),
            release => Assert.IsType<ReleaseSteeringEffect>(release));
    }

    [Fact]
    public void Planner_AHealthyGsx_ProducesNothing()
        => Assert.Empty(PushbackTickEffectPlanner.Plan(Reading(frozen: 0), Latches(staleTicks: 0), sequenceWasReset: false, sequenceTick: null));

    [Fact]
    public void Planner_OptionOff_ProducesNothing()
        => Assert.Empty(PushbackTickEffectPlanner.Plan(Reading(frozen: 1, option: false), Latches(staleTicks: 0), sequenceWasReset: false, sequenceTick: null));

    [Fact]
    public void Latches_CountTheRelease_AndStartOverWithTheSegment()
    {
        var latches = new PushbackTickLatches();
        latches.SetFrozenStaleTicks(5);
        latches.CountFrozenRelease();

        var counted = latches.Snapshot();
        Assert.Equal(1, counted.FrozenReleases);
        Assert.Equal(0, counted.FrozenStaleTicks);

        latches.ResetForSegment(GsxAutomationPhase.Flight);
        Assert.Equal(0, latches.Snapshot().FrozenReleases);
    }

    [Fact]
    public void TheWriteGate_AllowsTheHeldFlag_AndNoOtherFsdtVar()
    {
        Assert.True(SimWriteGate.IsAllowed("L:FSDT_VAR_Frozen"));
        Assert.False(SimWriteGate.IsAllowed("L:FSDT_VAR_EnginesStopped"));
    }
}
