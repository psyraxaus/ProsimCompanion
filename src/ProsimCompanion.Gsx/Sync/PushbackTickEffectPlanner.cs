using ProsimCompanion.Gsx.Automation;

namespace ProsimCompanion.Gsx.Sync;

/// <summary>
/// The pushback tick's effect planner, pure: (reading, latches, sequencer result) → the
/// ordered effects. Order: tug LVAR edges → new-segment reset → hooks (the pure hook core's
/// intents) → good-engine-start confirmation gate → gradual equipment removal → the beacon
/// sequence's transition and action. The beacon sequencer itself is host-owned state (like
/// the latches): the host resets and ticks it before planning and hands the result in, so
/// this function stays a pure function of what it is given.
/// </summary>
internal static class PushbackTickEffectPlanner
{
    // Good-engine-start confirmation (issue #106): once per push; a failed menu pick
    // retries every few ticks up to the cap, then leaves the menu to the pilot.
    internal const int ConfirmRetryGapTicks = 5;
    internal const int ConfirmMaxAttempts = 10;

    /// <summary>A new turnaround (or getting airborne) resets the sequence — once per phase.</summary>
    internal static bool ResetDue(GsxAutomationPhase phase, GsxAutomationPhase lastResetPhase)
        => phase is GsxAutomationPhase.Flight or GsxAutomationPhase.Arrival && lastResetPhase != phase;

    internal static IReadOnlyList<PushbackTickEffect> Plan(
        PushbackTickReading reading,
        PushbackLatchSnapshot latches,
        bool sequenceWasReset,
        PushbackTickResult? sequenceTick)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(latches);

        var effects = new List<PushbackTickEffect>();

        // Raw tug/pin progress in the decision log — this is how the state-12 engine-start
        // confirmation gate and pin removal become diagnosable from logs alone.
        if (reading.VehicleState != latches.LastVehicleState)
        {
            if (latches.LastVehicleState != -1 || reading.VehicleState != 0)
            {
                effects.Add(new PushbackDecisionEffect(
                    "pushback tug",
                    $"vehicle state {reading.VehicleState} ({DescribeVehicleState(reading.VehicleState)}); status={reading.PushbackStatus:F0}"));
            }
            effects.Add(new SetLastVehicleStateEffect(reading.VehicleState));
        }

        if (Math.Abs(reading.BypassPin - latches.LastBypassPin) > 0.5)
        {
            effects.Add(new SetLastBypassPinEffect(reading.BypassPin));
            effects.Add(new PushbackDecisionEffect("pushback tug", reading.BypassPin != 0 ? "bypass pin inserted" : "bypass pin removed"));
        }

        // A new turnaround (or getting airborne) resets the sequence.
        var resetDue = ResetDue(reading.Phase, latches.LastResetPhase);
        if (resetDue)
        {
            effects.Add(new ResetSegmentLatchesEffect(reading.Phase));
            if (sequenceWasReset)
            {
                effects.Add(new PushbackDecisionEffect("pushback sequence", "reset (new flight segment)"));
            }
        }

        var hookState = resetDue ? PushbackTickCore.HookState.Initial : latches.HookState;
        var engineStartConfirmed = !resetDue && latches.EngineStartConfirmed;
        var confirmCooldown = resetDue ? 0 : latches.ConfirmCooldown;

        // Tug-attached-during-boarding rule runs independently of the beacon sequence
        // (predecessor semantics — it exists precisely for the early-pushback-call flow).
        // The policy is the pure core (campaign #78); this shell executes its intents.
        if (reading.AutomationEnabled && reading.GsxReady)
        {
            PlanHooks(effects, reading, hookState);
            PlanEngineStartConfirmation(effects, reading, engineStartConfirmed, confirmCooldown);
        }

        // Gradual equipment removal belongs to the NON-sequence flow (the beacon sequence
        // owns its own timed removal step) and needs a ticker — this shell provides it.
        // Gated on departure services being complete (2026-09-25 cold-and-dark report:
        // the ticker ran the instant ground prep placed the GPU and pulled it again).
        if (PushbackTickCore.ShouldTickGradualRemoval(
                reading.BeaconSequenceEnabled,
                reading.AutomationEnabled,
                reading.DepartureComplete,
                reading.Phase))
        {
            effects.Add(new TickGradualRemovalEffect());
        }

        if (sequenceTick is { } tick)
        {
            if (tick.Transition is not null)
            {
                effects.Add(new PushbackDecisionEffect("pushback sequence", tick.Transition));
            }

            switch (tick.Action)
            {
                case PushbackAction.CloseDoors:
                    effects.Add(new CloseDoorsEffect());
                    break;
                case PushbackAction.RetractJetwayStairs:
                    effects.Add(new RetractJetwayStairsEffect());
                    break;
                case PushbackAction.RemoveGroundEquipment:
                    effects.Add(new RemoveGroundEquipmentEffect());
                    break;
                case PushbackAction.CallPushback:
                    effects.Add(new CallPushbackEffect());
                    break;
            }
        }

        return effects;
    }

    /// <summary>The Prosim2GSX departure phase-point hooks (#9) and the tug-attached auto-call,
    /// driven by the 1 Hz tick because they need a ticker and this shell already samples
    /// everything involved.</summary>
    private static void PlanHooks(List<PushbackTickEffect> effects, PushbackTickReading reading, PushbackTickCore.HookState hookState)
    {
        var intents = PushbackTickCore.Evaluate(hookState, reading.HookInputs);
        if (intents.State != hookState)
        {
            effects.Add(new SetHookStateEffect(intents.State));
        }

        if (intents.TugLatched)
        {
            effects.Add(new PushbackDecisionEffect("pushback tug", "tug attached during boarding — pushback auto-call armed"));
        }
        if (intents.CallTugPushback)
        {
            effects.Add(new PushbackDecisionEffect(
                "pushback tug",
                $"calling pushback — tug attached and {reading.HookInputs.CallPushbackWhenTugAttachedMode} condition met"));
            effects.Add(new CallPushbackEffect());
        }
        if (intents.RunDepartureJetwayStep)
        {
            effects.Add(new RunDepartureJetwayStepEffect());
        }
        if (intents.RemoveStairsAfterDeparture)
        {
            effects.Add(new RemoveStairsAfterDepartureEffect(reading.HookInputs.RemoveStairsAfterDepartureMode));
        }
        if (intents.CloseDoorsOnFinal)
        {
            effects.Add(new PushbackDecisionEffect("departure", "final loadsheet sent — closing doors"));
            effects.Add(new CloseDoorsEffect());
        }
        if (intents.RemoveJetwayStairsOnFinal)
        {
            effects.Add(new PushbackDecisionEffect("departure", "final loadsheet sent — removing jetway/stairs"));
            effects.Add(new RetractJetwayStairsEffect());
        }
    }

    /// <summary>The state-12 gate (issue #106): GSX finishes the physical push and waits for
    /// the good-engine-start confirmation on its "Interrupt pushback?" menu — the 2026-08-23
    /// flight sat there until the pilot answered by hand. Prosim2GSX-parity conditions: park
    /// brake set, an engine stably running (any, not both — single-engine taxi is a real
    /// procedure), none mid-start. Fails safe: an unmatched menu retries a few times, then
    /// the menu stays with the pilot.</summary>
    private static void PlanEngineStartConfirmation(
        List<PushbackTickEffect> effects, PushbackTickReading reading, bool engineStartConfirmed, int confirmCooldown)
    {
        if (engineStartConfirmed || reading.VehicleState != 12)
        {
            return;
        }

        // Switched off (2026-10-08): the menu stays with the pilot, nothing is logged per tick.
        if (!reading.ConfirmEngineStartOption)
        {
            return;
        }

        if (confirmCooldown > 0)
        {
            effects.Add(new DecrementConfirmCooldownEffect());
            return;
        }

        if (reading.Engines is not { } engines || !engines.AnyEngineRunning || engines.EngineStarting || !engines.ParkBrakeSet)
        {
            return;
        }

        effects.Add(new ConfirmEngineStartEffect());
    }

    internal static string DescribeVehicleState(int state) => state switch
    {
        0 => "idle",
        1 => "at gate",
        8 => "pushing back",
        11 => "waiting for engine shutdown",
        12 => "awaiting good-engine-start confirmation",
        13 => "disconnecting",
        14 => "clear to start",
        _ => "unknown",
    };
}
