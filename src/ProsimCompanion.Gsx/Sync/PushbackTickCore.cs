using ProsimCompanion.Gsx.Automation;

namespace ProsimCompanion.Gsx.Sync;

/// <summary>
/// The pushback shell's non-sequencer policy, pure (campaign #78): the tug-attached-during-
/// boarding latch (predecessor OnPushChange), the call-pushback-when-tug-attached modes, and
/// the departure phase-point hooks (#9: jetway connect during departure, stairs removal after
/// completion, doors-close + jetway-removal on the final loadsheet — the on-final pair yields
/// to the beacon sequence, which owns that timing when enabled). The beacon sequence itself is
/// already pure (<see cref="ProsimCompanion.Gsx.Automation.PushbackSequencer"/>); the shell
/// (<see cref="GsxPushbackSequenceService"/>) executes this core's intents.
/// </summary>
internal static class PushbackTickCore
{
    internal readonly record struct HookState(
        bool TugAttachedDuringBoarding,
        bool TugPushbackCalled,
        bool StairsRemovedAfterDeparture,
        bool DoorsClosedOnFinal,
        bool JetwayRemovedOnFinal)
    {
        internal static readonly HookState Initial = default;
    }

    internal sealed record HookInputs(
        bool DepartureStarted,
        bool DepartureComplete,
        bool FinalLoadsheetSent,
        bool BoardingCompleted,
        bool PushbackStatusRaised,
        bool BoardingRequestedOrActive,
        bool PushbackCallable,
        bool PushbackPending,
        bool PushbackAlreadyDone,
        bool CallJetwayStairsDuringDeparture,
        string RemoveStairsAfterDepartureMode,
        bool BeaconSequenceEnabled,
        bool CloseDoorsOnFinal,
        bool RemoveJetwayStairsOnFinal,
        string CallPushbackWhenTugAttachedMode);

    internal sealed record HookIntents(
        HookState State,
        bool TugLatched,
        bool CallTugPushback,
        bool RunDepartureJetwayStep,
        bool RemoveStairsAfterDeparture,
        bool CloseDoorsOnFinal,
        bool RemoveJetwayStairsOnFinal);

    /// <summary>Whether the shell should tick <see cref="GsxGroundEquipmentService.TickGradualRemovalAsync"/>
    /// this second: non-sequence flow only, automation on, a departure ground phase, and
    /// departure services complete. The last gate is the 2026-09-25 cold-and-dark fix — before
    /// it the ticker ran in Preparation the instant ground prep placed the GPU, saw external
    /// power still off (the crew had not pressed EXT PWR yet) and pulled the GPU straight back.</summary>
    internal static bool ShouldTickGradualRemoval(
        bool beaconSequenceEnabled,
        bool automationEnabled,
        bool departureComplete,
        GsxAutomationPhase phase)
        => !beaconSequenceEnabled
            && automationEnabled
            && departureComplete
            && phase is GsxAutomationPhase.Preparation or GsxAutomationPhase.PushBack;

    /// <summary>How long GSX's held flag must stand with the tug gone before it is cleared — long
    /// enough that GSX's own release (the normal case) always comes first.</summary>
    internal const int SteeringReleaseGraceTicks = 8;

    /// <summary>At most this many clears per flight segment; a GSX that keeps re-setting the
    /// flag is left alone after that (and the decision log says so).</summary>
    internal const int SteeringReleaseMaxPerSegment = 3;

    /// <summary>What the steering-release rule wants this tick.</summary>
    internal readonly record struct SteeringRelease(int StaleTicks, bool Release, bool GiveUp);

    /// <summary>
    /// The steering-release rule (2026-11-14, GSX Pro 4.0.23 on the owner's rig): GSX marks the
    /// aircraft held (<c>L:FSDT_VAR_Frozen</c> = 1) for the push and ProSim disconnects the nose
    /// wheel steering while it reads 1 — but GSX left it at 1 after the tug had gone, so the
    /// tiller was dead for the whole taxi. The flag is STALE when the aircraft is on the ground,
    /// the bypass pin is out and the tug is in a state where it holds nothing: 0 / 1 (idle),
    /// 14 (clear, hand signal) or 4 (driving away). Every state of an actual push (2, 3 and
    /// 5–13) and an inserted pin keep the count at zero, so a push in progress is never touched.
    /// Stale for <see cref="SteeringReleaseGraceTicks"/> seconds → clear it once; capped per
    /// segment. Independent of the beacon sequence and of who called the push.
    /// </summary>
    internal static SteeringRelease EvaluateSteeringRelease(
        bool optionOn, bool onGround, double frozen, double bypassPin, int vehicleState, int staleTicks, int releasesThisSegment)
    {
        var stale = optionOn && onGround && frozen > 0.5 && bypassPin < 0.5
            && vehicleState is 0 or 1 or 4 or 14;
        if (!stale)
        {
            return new(0, false, false);
        }

        var ticks = staleTicks + 1;
        if (ticks < SteeringReleaseGraceTicks)
        {
            return new(ticks, false, false);
        }

        if (releasesThisSegment >= SteeringReleaseMaxPerSegment)
        {
            // Said once, on the tick the grace runs out; the count then parks above it.
            return new(SteeringReleaseGraceTicks + 1, false, ticks == SteeringReleaseGraceTicks);
        }

        return new(0, true, false);
    }

    internal static HookIntents Evaluate(HookState state, HookInputs inputs)
    {
        // Predecessor OnPushChange rule: PUSHBACK_STATUS going nonzero while Boarding is
        // Requested/Active means the tug attached during boarding (the pilot answered the tug
        // question with yes, or attached it by hand). Latched until the next flight segment.
        var tugLatched = false;
        if (!state.TugAttachedDuringBoarding
            && inputs.PushbackStatusRaised
            && inputs.BoardingRequestedOrActive)
        {
            state = state with { TugAttachedDuringBoarding = true };
            tugLatched = true;
        }

        // Predecessor CallPushbackWhenTugAttached: with the tug already attached, call
        // Pushback once after departure services complete or after the final loadsheet.
        var callTugPushback = false;
        if (state.TugAttachedDuringBoarding && !state.TugPushbackCalled)
        {
            var due = inputs.CallPushbackWhenTugAttachedMode.ToLowerInvariant() switch
            {
                "afterdepartureservices" => inputs.DepartureComplete,
                "afterfinalloadsheet" => inputs.FinalLoadsheetSent,
                _ => false, // "never"
            };
            if (due && inputs.PushbackCallable && !inputs.PushbackPending && !inputs.PushbackAlreadyDone)
            {
                state = state with { TugPushbackCalled = true };
                callTugPushback = true;
            }
        }

        var runDepartureJetway = inputs.CallJetwayStairsDuringDeparture
            && inputs.DepartureStarted
            && !inputs.DepartureComplete;

        var removeStairs = false;
        if (!state.StairsRemovedAfterDeparture
            && inputs.DepartureComplete
            && !string.Equals(inputs.RemoveStairsAfterDepartureMode, "never", StringComparison.OrdinalIgnoreCase))
        {
            state = state with { StairsRemovedAfterDeparture = true };
            removeStairs = true;
        }

        // On-final hooks: fire once the final loadsheet is SENT and boarding has completed;
        // the beacon sequence owns that timing when enabled (predecessor rule).
        var closeDoors = false;
        var removeJetway = false;
        if (!inputs.BeaconSequenceEnabled && inputs.FinalLoadsheetSent && inputs.BoardingCompleted)
        {
            if (inputs.CloseDoorsOnFinal && !state.DoorsClosedOnFinal)
            {
                state = state with { DoorsClosedOnFinal = true };
                closeDoors = true;
            }
            if (inputs.RemoveJetwayStairsOnFinal && !state.JetwayRemovedOnFinal)
            {
                state = state with { JetwayRemovedOnFinal = true };
                removeJetway = true;
            }
        }

        return new HookIntents(
            state,
            tugLatched,
            callTugPushback,
            runDepartureJetway,
            removeStairs,
            closeDoors,
            removeJetway);
    }
}
