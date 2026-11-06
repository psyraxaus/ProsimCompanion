using ProsimCompanion.Core.State;
using ProsimCompanion.Gsx.Automation;
using ProsimCompanion.Gsx.Sync;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

/// <summary>
/// Table tests for the pushback tick's effect planner (architecture review C1, the seam's
/// second adapter). Order: tug LVAR edges → new-segment reset → hooks → good-engine-start
/// confirmation → gradual removal → the beacon sequence's transition and action. The shell
/// had zero tests before; these pin what its tick did inline.
/// </summary>
public sealed class PushbackTickEffectPlannerTests
{
    private static PushbackTickCore.HookInputs QuietHooks()
        => new(
            DepartureStarted: false,
            DepartureComplete: false,
            FinalLoadsheetSent: false,
            BoardingCompleted: false,
            PushbackStatusRaised: false,
            BoardingRequestedOrActive: false,
            PushbackCallable: true,
            PushbackPending: false,
            PushbackAlreadyDone: false,
            CallJetwayStairsDuringDeparture: false,
            RemoveStairsAfterDepartureMode: "never",
            BeaconSequenceEnabled: true,
            CloseDoorsOnFinal: true,
            RemoveJetwayStairsOnFinal: true,
            CallPushbackWhenTugAttachedMode: "afterDepartureServices");

    private static PushbackTickReading Reading(
        GsxAutomationPhase phase = GsxAutomationPhase.Preparation,
        bool automationEnabled = true,
        bool gsxReady = true,
        bool beaconSequence = true,
        bool departureComplete = false,
        int vehicleState = 0,
        double pushbackStatus = 0,
        double bypassPin = 0,
        PushbackTickCore.HookInputs? hooks = null,
        bool confirmOption = true,
        EngineStartEvidence? engines = null)
        => new(
            Phase: phase,
            AutomationEnabled: automationEnabled,
            GsxReady: gsxReady,
            BeaconSequenceEnabled: beaconSequence,
            DepartureComplete: departureComplete,
            VehicleState: vehicleState,
            PushbackStatus: pushbackStatus,
            BypassPin: bypassPin,
            HookInputs: hooks ?? QuietHooks(),
            SequenceInputs: new PushbackSequencer.Inputs(false, false, false, false, true, true),
            Delays: new PushbackSequencer.Delays(1, 5, 1, 5, 1, 5),
            ConfirmEngineStartOption: confirmOption,
            Engines: engines);

    /// <summary>The latches after the first tick ever saw the tug idle (state 0 logged nothing).</summary>
    private static PushbackLatchSnapshot Settled(
        GsxAutomationPhase lastResetPhase = GsxAutomationPhase.SessionStart,
        int lastVehicleState = 0,
        double lastPushbackStatus = 0,
        PushbackTickCore.HookState? hookState = null,
        bool confirmed = false,
        int cooldown = 0)
        => PushbackLatchSnapshot.Initial with
        {
            LastResetPhase = lastResetPhase,
            LastVehicleState = lastVehicleState,
            LastPushbackStatus = lastPushbackStatus,
            HookState = hookState ?? PushbackTickCore.HookState.Initial,
            EngineStartConfirmed = confirmed,
            ConfirmCooldown = cooldown,
        };

    private static IReadOnlyList<PushbackTickEffect> Plan(
        PushbackTickReading reading, PushbackLatchSnapshot? latches = null, bool sequenceWasReset = false, PushbackTickResult? tick = null)
        => PushbackTickEffectPlanner.Plan(reading, latches ?? Settled(), sequenceWasReset, tick);

    // ---- tug LVAR monitor ----

    [Fact]
    public void FirstReading_OfAnIdleTug_LatchesSilently()
    {
        var effects = Plan(Reading(vehicleState: 0), PushbackLatchSnapshot.Initial);

        Assert.Equal([new SetLastVehicleStateEffect(0), new SetLastPushbackStatusEffect(0)], effects);
    }

    [Fact]
    public void VehicleStateEdge_LogsThenLatches_WithTheStatusFigure()
    {
        var effects = Plan(Reading(vehicleState: 12, pushbackStatus: 3));

        Assert.Equal(
            [
                new PushbackDecisionEffect("pushback tug", "vehicle state 12 (awaiting good-engine-start confirmation); status=3"),
                new SetLastVehicleStateEffect(12),
                new SetLastPushbackStatusEffect(3),
                new PushbackDecisionEffect("pushback tug", "pushback status 3"),
                new PushbackStageEffect(PushbackStage.TugConnected),
            ],
            effects);
    }

    // ---- the crew's stages (owner request 2026-11-07, plan V2) ----

    [Theory]
    [InlineData(8, PushbackStage.Pushing)]
    [InlineData(11, PushbackStage.PushComplete)]
    [InlineData(13, PushbackStage.Disconnecting)]
    [InlineData(14, PushbackStage.Clear)]
    public void VehicleStateEdge_RaisesTheCrewStage_AfterTheLatch(int state, PushbackStage stage)
    {
        var effects = Plan(Reading(vehicleState: state), Settled(lastVehicleState: 1));

        Assert.Equal(new SetLastVehicleStateEffect(state), effects[1]);
        Assert.Equal(new PushbackStageEffect(stage), effects[2]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(12)]
    public void VehicleStates_NobodySpeaksOn_RaiseNoStage(int state)
        => Assert.DoesNotContain(Plan(Reading(vehicleState: state), Settled(lastVehicleState: 0)), e => e is PushbackStageEffect);

    [Fact]
    public void FirstReadOfAPushingTug_LatchesWithoutAStage_SoARestartNeverReplaysTheLines()
    {
        var effects = Plan(Reading(vehicleState: 8, pushbackStatus: 3), PushbackLatchSnapshot.Initial);

        Assert.DoesNotContain(effects, e => e is PushbackStageEffect);
        Assert.Contains(new SetLastPushbackStatusEffect(3), effects);
    }

    [Theory]
    [InlineData(0, 3, true)]
    [InlineData(0, 4, true)]
    [InlineData(3, 4, false)]  // still connected — no second line
    [InlineData(0, 1, false)]  // called, not yet connected
    [InlineData(3, 0, false)]
    public void PushbackStatusEdge_RaisesTugConnected_OnceOnTheWayIn(double last, double now, bool tugConnected)
    {
        var effects = Plan(Reading(pushbackStatus: now), Settled(lastPushbackStatus: last));

        Assert.Contains(new SetLastPushbackStatusEffect(now), effects);
        Assert.Contains(new PushbackDecisionEffect("pushback tug", $"pushback status {now:F0}"), effects);
        Assert.Equal(tugConnected, effects.Contains(new PushbackStageEffect(PushbackStage.TugConnected)));
    }

    [Fact]
    public void BypassPinEdge_LatchesThenLogs_OnlyPastHalfAUnit()
    {
        Assert.Empty(Plan(Reading(bypassPin: 0.4)));

        var inserted = Plan(Reading(bypassPin: 1));
        Assert.Equal([new SetLastBypassPinEffect(1), new PushbackDecisionEffect("pushback tug", "bypass pin inserted")], inserted);

        var removed = Plan(Reading(bypassPin: 0), Settled() with { LastBypassPin = 1 });
        Assert.Equal([new SetLastBypassPinEffect(0), new PushbackDecisionEffect("pushback tug", "bypass pin removed")], removed);
    }

    [Theory]
    [InlineData(1, "at gate")]
    [InlineData(8, "pushing back")]
    [InlineData(13, "disconnecting")]
    [InlineData(14, "clear to start")]
    [InlineData(99, "unknown")]
    public void VehicleStates_AreDescribed(int state, string text)
        => Assert.Equal(text, PushbackTickEffectPlanner.DescribeVehicleState(state));

    // ---- new flight segment ----

    [Fact]
    public void FlightOrArrival_ResetsThePerPushLatches_OncePerPhase()
    {
        var airborne = Plan(Reading(phase: GsxAutomationPhase.Flight), Settled(), sequenceWasReset: true);
        Assert.Equal(
            [new ResetSegmentLatchesEffect(GsxAutomationPhase.Flight), new PushbackDecisionEffect("pushback sequence", "reset (new flight segment)")],
            airborne);

        var again = Plan(Reading(phase: GsxAutomationPhase.Flight), Settled(lastResetPhase: GsxAutomationPhase.Flight));
        Assert.Empty(again);

        var idleSequencer = Plan(Reading(phase: GsxAutomationPhase.Arrival), Settled(lastResetPhase: GsxAutomationPhase.Flight), sequenceWasReset: false);
        Assert.Equal([new ResetSegmentLatchesEffect(GsxAutomationPhase.Arrival)], idleSequencer);

        Assert.False(PushbackTickEffectPlanner.ResetDue(GsxAutomationPhase.Preparation, GsxAutomationPhase.SessionStart));
        Assert.True(PushbackTickEffectPlanner.ResetDue(GsxAutomationPhase.Arrival, GsxAutomationPhase.Flight));
    }

    [Fact]
    public void Reset_ClearsTheHookStateTheSameTickSeesIt()
    {
        // A tug latched last segment must not call pushback on the first Arrival tick.
        var latched = new PushbackTickCore.HookState(TugAttachedDuringBoarding: true, false, false, false, false);
        var hooks = QuietHooks() with { DepartureComplete = true };

        var effects = Plan(Reading(phase: GsxAutomationPhase.Arrival, hooks: hooks), Settled(hookState: latched));

        Assert.Empty(effects.OfType<CallPushbackEffect>());
        Assert.Empty(effects.OfType<SetHookStateEffect>());
    }

    // ---- hooks (PushbackTickCore intents → effects) ----

    [Fact]
    public void TugAttachedDuringBoarding_LatchesStateThenLogs()
    {
        var hooks = QuietHooks() with { PushbackStatusRaised = true, BoardingRequestedOrActive = true };

        var effects = Plan(Reading(hooks: hooks));

        Assert.Equal(
            [
                new SetHookStateEffect(new PushbackTickCore.HookState(true, false, false, false, false)),
                new PushbackDecisionEffect("pushback tug", "tug attached during boarding — pushback auto-call armed"),
            ],
            effects);
    }

    [Fact]
    public void TugAttached_DepartureComplete_CallsPushback_StateBeforeDecisionBeforeCall()
    {
        var latched = new PushbackTickCore.HookState(TugAttachedDuringBoarding: true, false, false, false, false);
        var hooks = QuietHooks() with { DepartureComplete = true };

        var effects = Plan(Reading(hooks: hooks), Settled(hookState: latched));

        Assert.Equal(
            [
                new SetHookStateEffect(latched with { TugPushbackCalled = true }),
                new PushbackDecisionEffect("pushback tug", "calling pushback — tug attached and afterDepartureServices condition met"),
                new CallPushbackEffect(),
            ],
            effects.Take(3).ToArray());
    }

    [Fact]
    public void OnFinalHooks_LogThenAct_DoorsBeforeJetway_NonSequenceFlowOnly()
    {
        var hooks = QuietHooks() with { FinalLoadsheetSent = true, BoardingCompleted = true, BeaconSequenceEnabled = false };

        var effects = Plan(Reading(beaconSequence: false, hooks: hooks));

        Assert.Equal(
            [
                new SetHookStateEffect(new PushbackTickCore.HookState(false, false, false, true, true)),
                new PushbackDecisionEffect("departure", "final loadsheet sent — closing doors"),
                new CloseDoorsEffect(),
                new PushbackDecisionEffect("departure", "final loadsheet sent — removing jetway/stairs"),
                new RetractJetwayStairsEffect(),
            ],
            effects);

        // The beacon sequence owns that timing when enabled.
        Assert.Empty(Plan(Reading(hooks: hooks with { BeaconSequenceEnabled = true })).OfType<CloseDoorsEffect>());
    }

    [Fact]
    public void DepartureHooks_JetwayStepAndStairsRemoval()
    {
        var running = QuietHooks() with { DepartureStarted = true, CallJetwayStairsDuringDeparture = true };
        Assert.Contains(Plan(Reading(hooks: running)), e => e is RunDepartureJetwayStepEffect);

        var done = QuietHooks() with { DepartureStarted = true, DepartureComplete = true, RemoveStairsAfterDepartureMode = "onlyJetway" };
        var effects = Plan(Reading(hooks: done));
        Assert.Contains(new RemoveStairsAfterDepartureEffect("onlyJetway"), effects);
        Assert.DoesNotContain(effects, e => e is RunDepartureJetwayStepEffect);
    }

    [Fact]
    public void Hooks_HoldWhileAutomationOffOrGsxNotReady()
    {
        var hooks = QuietHooks() with { PushbackStatusRaised = true, BoardingRequestedOrActive = true };

        Assert.Empty(Plan(Reading(automationEnabled: false, hooks: hooks)));
        Assert.Empty(Plan(Reading(gsxReady: false, hooks: hooks)));
    }

    // ---- good-engine-start confirmation (issue #106) ----

    private static readonly EngineStartEvidence ReadyToConfirm = new(AnyEngineRunning: true, EngineStarting: false, ParkBrakeSet: true);

    [Fact]
    public void State12_EngineRunningParkBrakeSet_Confirms()
    {
        var effects = Plan(Reading(vehicleState: 12, engines: ReadyToConfirm), Settled(lastVehicleState: 12));

        Assert.Equal([new ConfirmEngineStartEffect()], effects);
    }

    [Theory]
    [InlineData(false, false, true)]  // no engine running yet
    [InlineData(true, true, true)]    // one still starting
    [InlineData(true, false, false)]  // park brake off
    public void State12_WaitsForTheProsim2GsxConditions(bool running, bool starting, bool parkBrake)
    {
        var effects = Plan(Reading(vehicleState: 12, engines: new EngineStartEvidence(running, starting, parkBrake)), Settled(lastVehicleState: 12));

        Assert.Empty(effects);
    }

    [Fact]
    public void State12_NoFlightData_OrOtherState_OrAlreadyConfirmed_DoesNothing()
    {
        Assert.Empty(Plan(Reading(vehicleState: 12, engines: null), Settled(lastVehicleState: 12)));
        Assert.Empty(Plan(Reading(vehicleState: 8, engines: ReadyToConfirm), Settled(lastVehicleState: 8)));
        Assert.Empty(Plan(Reading(vehicleState: 12, engines: ReadyToConfirm), Settled(lastVehicleState: 12, confirmed: true)));
    }

    [Fact]
    public void State12_SwitchedOff_LeavesTheMenuToThePilot_Silently()
    {
        // 2026-10-08: nothing is logged per tick.
        Assert.Empty(Plan(Reading(vehicleState: 12, engines: ReadyToConfirm, confirmOption: false), Settled(lastVehicleState: 12)));
    }

    [Fact]
    public void State12_InCooldown_CountsDownInsteadOfRetrying()
    {
        var effects = Plan(Reading(vehicleState: 12, engines: ReadyToConfirm), Settled(lastVehicleState: 12, cooldown: 3));

        Assert.Equal([new DecrementConfirmCooldownEffect()], effects);
    }

    [Fact]
    public void Confirmation_RunsAfterTheHooks_BeforeTheSequence()
    {
        var hooks = QuietHooks() with { PushbackStatusRaised = true, BoardingRequestedOrActive = true };
        var tick = new PushbackTickResult(PushbackAction.None, "beacon on — waiting for APU");

        var effects = Plan(Reading(vehicleState: 12, engines: ReadyToConfirm, hooks: hooks), Settled(lastVehicleState: 12), tick: tick);

        Assert.Equal(
            [
                typeof(SetHookStateEffect),
                typeof(PushbackDecisionEffect),
                typeof(ConfirmEngineStartEffect),
                typeof(PushbackDecisionEffect),
            ],
            effects.Select(e => e.GetType()).ToArray());
    }

    // ---- gradual removal and the beacon sequence ----

    [Fact]
    public void GradualRemoval_TicksOnlyInTheNonSequenceFlow_OnceDepartureIsComplete()
    {
        Assert.Equal([new TickGradualRemovalEffect()], Plan(Reading(beaconSequence: false, departureComplete: true)));
        Assert.Empty(Plan(Reading(beaconSequence: false, departureComplete: false)));
        Assert.Empty(Plan(Reading(beaconSequence: true, departureComplete: true)));
        Assert.Empty(Plan(Reading(beaconSequence: false, departureComplete: true, phase: GsxAutomationPhase.Flight), Settled(lastResetPhase: GsxAutomationPhase.Flight)));
    }

    [Theory]
    [InlineData(PushbackAction.CloseDoors, typeof(CloseDoorsEffect))]
    [InlineData(PushbackAction.RetractJetwayStairs, typeof(RetractJetwayStairsEffect))]
    [InlineData(PushbackAction.RemoveGroundEquipment, typeof(RemoveGroundEquipmentEffect))]
    [InlineData(PushbackAction.CallPushback, typeof(CallPushbackEffect))]
    public void SequenceTick_TransitionIsLogged_ThenTheActionFollows(PushbackAction action, Type effect)
    {
        var tick = new PushbackTickResult(action, "closing doors; jetway/stairs retract in 3s");

        var effects = Plan(Reading(), tick: tick);

        Assert.Equal(new PushbackDecisionEffect("pushback sequence", "closing doors; jetway/stairs retract in 3s"), effects[0]);
        Assert.IsType(effect, effects[1]);
    }

    /// <summary>The crew says "bridge is off, doors closed" (owner request 2026-11-07, plan V2): the
    /// doors and jetway steps carry their stage after the action; the other steps do not.</summary>
    [Theory]
    [InlineData(PushbackAction.CloseDoors, PushbackStage.DoorsClosed)]
    [InlineData(PushbackAction.RetractJetwayStairs, PushbackStage.BridgeRemoved)]
    [InlineData(PushbackAction.RemoveGroundEquipment, null)]
    [InlineData(PushbackAction.CallPushback, null)]
    public void SequenceTick_DoorsAndJetwaySteps_CarryTheCrewStage(PushbackAction action, PushbackStage? stage)
    {
        var effects = Plan(Reading(), tick: new PushbackTickResult(action, null));

        var stages = effects.OfType<PushbackStageEffect>().Select(e => e.Stage).ToArray();
        Assert.Equal(stage is { } s ? [s] : [], stages);
        if (stage is not null)
        {
            Assert.IsType<PushbackStageEffect>(effects[^1]);
        }
    }

    [Fact]
    public void SequenceTick_WithoutTransitionOrAction_PlansNothing()
    {
        Assert.Empty(Plan(Reading(), tick: default(PushbackTickResult)));
    }

    [Fact]
    public void SequenceRuns_OnlyWithAutomationAndSequenceOnAndGsxReady()
    {
        Assert.True(Reading().SequenceRuns);
        Assert.False(Reading(automationEnabled: false).SequenceRuns);
        Assert.False(Reading(beaconSequence: false).SequenceRuns);
        Assert.False(Reading(gsxReady: false).SequenceRuns);
    }
}
