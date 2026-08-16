using ProsimCompanion.Gsx.Sync;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

/// <summary>Table tests for the pushback shell's hook policy (campaign #78).</summary>
public sealed class PushbackTickCoreTests
{
    private static PushbackTickCore.HookInputs Quiet()
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
            RemoveStairsAfterDepartureMode: "always",
            BeaconSequenceEnabled: false,
            CloseDoorsOnFinal: true,
            RemoveJetwayStairsOnFinal: true,
            CallPushbackWhenTugAttachedMode: "afterDepartureServices");

    [Fact]
    public void TugDuringBoarding_LatchesOnce()
    {
        var inputs = Quiet() with { PushbackStatusRaised = true, BoardingRequestedOrActive = true };

        var first = PushbackTickCore.Evaluate(PushbackTickCore.HookState.Initial, inputs);
        Assert.True(first.TugLatched);
        Assert.True(first.State.TugAttachedDuringBoarding);

        var second = PushbackTickCore.Evaluate(first.State, inputs);
        Assert.False(second.TugLatched);
    }

    [Fact]
    public void TugStatus_OutsideBoarding_NeverLatches()
    {
        var outcome = PushbackTickCore.Evaluate(
            PushbackTickCore.HookState.Initial,
            Quiet() with { PushbackStatusRaised = true });

        Assert.False(outcome.State.TugAttachedDuringBoarding);
    }

    [Theory]
    [InlineData("afterDepartureServices", true, false, true)]
    [InlineData("afterDepartureServices", false, true, false)]
    [InlineData("afterFinalLoadsheet", false, true, true)]
    [InlineData("never", true, true, false)]
    public void TugPushback_FiresOnItsConfiguredCondition(
        string mode, bool departureComplete, bool finalSent, bool expectCall)
    {
        var state = PushbackTickCore.HookState.Initial with { TugAttachedDuringBoarding = true };
        var inputs = Quiet() with
        {
            CallPushbackWhenTugAttachedMode = mode,
            DepartureComplete = departureComplete,
            FinalLoadsheetSent = finalSent,
        };

        var outcome = PushbackTickCore.Evaluate(state, inputs);

        Assert.Equal(expectCall, outcome.CallTugPushback);
        Assert.Equal(expectCall, outcome.State.TugPushbackCalled);
    }

    [Fact]
    public void TugPushback_HoldsWhileNotCallable_OrPendingOrDone()
    {
        var state = PushbackTickCore.HookState.Initial with { TugAttachedDuringBoarding = true };
        var due = Quiet() with { DepartureComplete = true };

        Assert.False(PushbackTickCore.Evaluate(state, due with { PushbackCallable = false }).CallTugPushback);
        Assert.False(PushbackTickCore.Evaluate(state, due with { PushbackPending = true }).CallTugPushback);
        Assert.False(PushbackTickCore.Evaluate(state, due with { PushbackAlreadyDone = true }).CallTugPushback);
        // The latch stays un-consumed so a later tick can still fire.
        Assert.False(PushbackTickCore.Evaluate(state, due with { PushbackCallable = false }).State.TugPushbackCalled);
    }

    [Fact]
    public void DepartureJetwayStep_RunsOnlyWhileDepartureIsRunning()
    {
        var option = Quiet() with { CallJetwayStairsDuringDeparture = true };

        Assert.False(PushbackTickCore.Evaluate(default, option).RunDepartureJetwayStep);
        Assert.True(PushbackTickCore.Evaluate(default, option with { DepartureStarted = true }).RunDepartureJetwayStep);
        Assert.False(PushbackTickCore.Evaluate(
            default,
            option with { DepartureStarted = true, DepartureComplete = true }).RunDepartureJetwayStep);
    }

    [Fact]
    public void StairsRemoval_FiresOncePerCycle_UnlessModeIsNever()
    {
        var done = Quiet() with { DepartureComplete = true };

        var first = PushbackTickCore.Evaluate(default, done);
        Assert.True(first.RemoveStairsAfterDeparture);

        var second = PushbackTickCore.Evaluate(first.State, done);
        Assert.False(second.RemoveStairsAfterDeparture);

        Assert.False(PushbackTickCore.Evaluate(
            default,
            done with { RemoveStairsAfterDepartureMode = "never" }).RemoveStairsAfterDeparture);
    }

    [Fact]
    public void OnFinalHooks_NeedFinalSentAndBoardingComplete_AndYieldToTheBeaconSequence()
    {
        var final = Quiet() with { FinalLoadsheetSent = true, BoardingCompleted = true };

        var fired = PushbackTickCore.Evaluate(default, final);
        Assert.True(fired.CloseDoorsOnFinal);
        Assert.True(fired.RemoveJetwayStairsOnFinal);

        // Once each per cycle.
        var again = PushbackTickCore.Evaluate(fired.State, final);
        Assert.False(again.CloseDoorsOnFinal);
        Assert.False(again.RemoveJetwayStairsOnFinal);

        // The beacon sequence owns the on-final timing when enabled (predecessor rule).
        Assert.False(PushbackTickCore.Evaluate(
            default,
            final with { BeaconSequenceEnabled = true }).CloseDoorsOnFinal);

        Assert.False(PushbackTickCore.Evaluate(
            default,
            final with { BoardingCompleted = false }).CloseDoorsOnFinal);
    }
}
