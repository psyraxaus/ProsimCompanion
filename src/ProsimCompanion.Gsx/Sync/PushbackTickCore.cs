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
