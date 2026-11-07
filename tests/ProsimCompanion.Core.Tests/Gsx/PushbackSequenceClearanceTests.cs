using ProsimCompanion.Core.Radios;
using ProsimCompanion.Gsx.Automation;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

/// <summary>The ATC step of the beacon-orchestrated pushback (owner request 2026-11-07):
/// ready-for-push → "request push and start" → approval or timeout → the GSX call.</summary>
public sealed class PushbackSequenceClearanceTests
{
    private static readonly PushbackSequencer.Delays Delays = new(1, 5, 1, 5, 1, 5);

    private static PushbackSequencer.Inputs Ready(
        PushClearanceState clearance = PushClearanceState.None,
        bool requestClearance = true,
        int waitSec = 15,
        bool beacon = true,
        bool callable = true)
        => new(Armed: true, BeaconOn: beacon, ApuRunning: true, AnyDoorOpen: false,
            CallPushback: true, PushbackCallable: callable,
            RequestClearance: requestClearance, Clearance: clearance, ClearanceWaitSec: waitSec);

    /// <summary>Walks a fresh sequencer to ReadyForPush (2 s per delayed step).</summary>
    private static PushbackSequencer AtReadyForPush()
    {
        var sequencer = new PushbackSequencer((_, _) => 2);
        for (var i = 0; i < 20 && sequencer.Step != PushbackSequenceStep.ReadyForPush; i++)
        {
            sequencer.Tick(Ready(), Delays);
        }

        Assert.Equal(PushbackSequenceStep.ReadyForPush, sequencer.Step);
        return sequencer;
    }

    [Fact]
    public void ReadyForPush_AsksATCFirst_ThenCallsPushbackOnTheApproval()
    {
        var sequencer = AtReadyForPush();

        var request = sequencer.Tick(Ready(), Delays);
        Assert.Equal(PushbackAction.RequestPushClearance, request.Action);
        Assert.Equal(PushbackSequenceStep.AwaitingPushClearance, sequencer.Step);
        Assert.Contains("requesting push and start", request.Transition);

        // ATC quiet: nothing happens, no second request.
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(PushbackAction.None, sequencer.Tick(Ready(PushClearanceState.Requested), Delays).Action);
        }

        var approved = sequencer.Tick(Ready(PushClearanceState.Approved), Delays);
        Assert.Equal(PushbackAction.None, approved.Action);
        Assert.Contains("approved", approved.Transition);
        Assert.Equal(PushbackSequenceStep.ReadyForPush, sequencer.Step);

        var call = sequencer.Tick(Ready(PushClearanceState.Approved), Delays);
        Assert.Equal(PushbackAction.CallPushback, call.Action);
        Assert.Equal(PushbackSequenceStep.PushbackCalled, sequencer.Step);
    }

    [Fact]
    public void AwaitingClearance_ASilentATC_PushesAtTheTimeout()
    {
        var sequencer = AtReadyForPush();
        Assert.Equal(PushbackAction.RequestPushClearance, sequencer.Tick(Ready(waitSec: 15), Delays).Action);

        PushbackTickResult last = default;
        var ticks = 0;
        while (sequencer.Step == PushbackSequenceStep.AwaitingPushClearance && ticks++ < 40)
        {
            last = sequencer.Tick(Ready(PushClearanceState.Requested, waitSec: 15), Delays);
        }

        Assert.Equal(15, ticks);
        Assert.Contains("no push approval", last.Transition);
        Assert.Equal(PushbackAction.CallPushback, sequencer.Tick(Ready(PushClearanceState.Requested), Delays).Action);
    }

    [Fact]
    public void AwaitingClearance_AHoldIsNotedOnce_AndWaitedThrough()
    {
        var sequencer = AtReadyForPush();
        sequencer.Tick(Ready(waitSec: 60), Delays);

        var held = sequencer.Tick(Ready(PushClearanceState.Held, waitSec: 60), Delays);
        Assert.Contains("holds the push", held.Transition);
        Assert.Null(sequencer.Tick(Ready(PushClearanceState.Held, waitSec: 60), Delays).Transition);

        Assert.Contains("approved", sequencer.Tick(Ready(PushClearanceState.Approved, waitSec: 60), Delays).Transition);
        Assert.Equal(PushbackAction.CallPushback, sequencer.Tick(Ready(PushClearanceState.Approved, waitSec: 60), Delays).Action);
    }

    [Fact]
    public void AClearanceTheCaptainAlreadyHas_SkipsTheRequest()
    {
        var sequencer = AtReadyForPush();

        var tick = sequencer.Tick(Ready(PushClearanceState.Approved), Delays);
        Assert.Equal(PushbackAction.None, tick.Action);
        Assert.Contains("already approved", tick.Transition);
        Assert.Equal(PushbackAction.CallPushback, sequencer.Tick(Ready(PushClearanceState.Approved), Delays).Action);
    }

    [Fact]
    public void ARequestTheCaptainAlreadyMade_IsWaitedOn_NotRepeated()
    {
        var sequencer = AtReadyForPush();

        var tick = sequencer.Tick(Ready(PushClearanceState.Requested), Delays);
        Assert.Equal(PushbackAction.None, tick.Action);
        Assert.Contains("already requested", tick.Transition);
        Assert.Equal(PushbackSequenceStep.AwaitingPushClearance, sequencer.Step);
    }

    [Fact]
    public void WithoutTheRadio_OrTheOptionOff_ThePushIsCalledAsBefore()
    {
        var sequencer = AtReadyForPush();

        Assert.Equal(PushbackAction.CallPushback, sequencer.Tick(Ready(requestClearance: false), Delays).Action);
    }

    [Fact]
    public void BeaconOff_PausesTheWait_AndTheApprovalKeeps()
    {
        var sequencer = AtReadyForPush();
        sequencer.Tick(Ready(waitSec: 15), Delays);

        for (var i = 0; i < 30; i++)
        {
            Assert.Equal(default, sequencer.Tick(Ready(PushClearanceState.Requested, beacon: false), Delays));
        }

        Assert.Equal(PushbackSequenceStep.AwaitingPushClearance, sequencer.Step);
        Assert.Contains("approved", sequencer.Tick(Ready(PushClearanceState.Approved), Delays).Transition);
    }

    [Fact]
    public void Approved_ButGsxNotCallableYet_WaitsForTheServiceWithoutAskingAgain()
    {
        var sequencer = AtReadyForPush();
        sequencer.Tick(Ready(), Delays);
        sequencer.Tick(Ready(PushClearanceState.Approved), Delays);

        var waiting = sequencer.Tick(Ready(PushClearanceState.Approved, callable: false), Delays);
        Assert.Equal(PushbackAction.None, waiting.Action);
        Assert.Contains("waiting for the GSX Departure service", waiting.Transition);

        Assert.Equal(PushbackAction.CallPushback, sequencer.Tick(Ready(PushClearanceState.Approved), Delays).Action);
    }

    [Fact]
    public void Reset_ForgetsTheSettledClearance()
    {
        var sequencer = AtReadyForPush();
        sequencer.Tick(Ready(PushClearanceState.Approved), Delays);
        sequencer.Reset();

        var again = AtReadyForPushFrom(sequencer);
        Assert.Equal(PushbackAction.RequestPushClearance, again.Tick(Ready(), Delays).Action);
    }

    private static PushbackSequencer AtReadyForPushFrom(PushbackSequencer sequencer)
    {
        for (var i = 0; i < 20 && sequencer.Step != PushbackSequenceStep.ReadyForPush; i++)
        {
            sequencer.Tick(Ready(), Delays);
        }

        Assert.Equal(PushbackSequenceStep.ReadyForPush, sequencer.Step);
        return sequencer;
    }
}
