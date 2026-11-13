using ProsimCompanion.Gsx.Automation;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

public sealed class PushbackSequencerTests
{
    // Fixed 2 s per delayed step keeps the walkthroughs deterministic and short.
    private static PushbackSequencer NewSequencer() => new((_, _) => 2);

    private static readonly PushbackSequencer.Delays Delays = new(1, 5, 1, 5, 1, 5);

    private static PushbackSequencer.Inputs Ready(
        bool beacon = true,
        bool apu = true,
        bool doorsOpen = true,
        bool callPushback = true,
        bool pushbackCallable = true)
        => new(Armed: true, beacon, apu, doorsOpen, callPushback, pushbackCallable);

    private static PushbackAction TickUntilAction(PushbackSequencer sequencer, PushbackSequencer.Inputs inputs, int maxTicks = 10)
    {
        for (var i = 0; i < maxTicks; i++)
        {
            var result = sequencer.Tick(inputs, Delays);
            if (result.Action != PushbackAction.None)
            {
                return result.Action;
            }
        }
        return PushbackAction.None;
    }

    [Fact]
    public void FullSequence_RunsInPredecessorOrder()
    {
        var sequencer = NewSequencer();

        Assert.Equal(PushbackSequenceStep.Idle, sequencer.Step);
        _ = sequencer.Tick(Ready(), Delays);                              // beacon on → wait APU
        Assert.Equal(PushbackSequenceStep.WaitingForApu, sequencer.Step);
        _ = sequencer.Tick(Ready(), Delays);                              // APU running → doors delay
        Assert.Equal(PushbackSequenceStep.DoorsStep, sequencer.Step);

        Assert.Equal(PushbackAction.CloseDoors, TickUntilAction(sequencer, Ready()));
        Assert.Equal(PushbackSequenceStep.JetwayStep, sequencer.Step);
        Assert.Equal(PushbackAction.RetractJetwayStairs, TickUntilAction(sequencer, Ready()));
        Assert.Equal(PushbackSequenceStep.GpuStep, sequencer.Step);
        Assert.Equal(PushbackAction.RemoveGroundEquipment, TickUntilAction(sequencer, Ready()));
        Assert.Equal(PushbackSequenceStep.ReadyForPush, sequencer.Step);
        Assert.Equal(PushbackAction.CallPushback, TickUntilAction(sequencer, Ready()));
        Assert.Equal(PushbackSequenceStep.PushbackCalled, sequencer.Step);
    }

    [Fact]
    public void NotArmed_NeverStarts()
    {
        var sequencer = NewSequencer();

        var result = sequencer.Tick(Ready() with { Armed = false }, Delays);

        Assert.Equal(PushbackAction.None, result.Action);
        Assert.Equal(PushbackSequenceStep.Idle, sequencer.Step);
    }

    [Fact]
    public void BeaconOff_PausesMidSequence()
    {
        var sequencer = NewSequencer();
        _ = sequencer.Tick(Ready(), Delays);
        _ = sequencer.Tick(Ready(), Delays); // in DoorsStep

        for (var i = 0; i < 20; i++)
        {
            var result = sequencer.Tick(Ready(beacon: false), Delays);
            Assert.Equal(PushbackAction.None, result.Action);
        }
        Assert.Equal(PushbackSequenceStep.DoorsStep, sequencer.Step);

        // Beacon back on resumes where it left off.
        Assert.Equal(PushbackAction.CloseDoors, TickUntilAction(sequencer, Ready()));
    }

    [Fact]
    public void ApuNotRunning_BlocksBeforeDoors()
    {
        var sequencer = NewSequencer();
        _ = sequencer.Tick(Ready(apu: false), Delays);
        Assert.Equal(PushbackSequenceStep.WaitingForApu, sequencer.Step);

        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(PushbackAction.None, sequencer.Tick(Ready(apu: false), Delays).Action);
        }
        Assert.Equal(PushbackSequenceStep.WaitingForApu, sequencer.Step);
    }

    [Fact]
    public void DoorsAlreadyClosed_SkipsTheCloseAction()
    {
        var sequencer = NewSequencer();
        _ = sequencer.Tick(Ready(doorsOpen: false), Delays);
        _ = sequencer.Tick(Ready(doorsOpen: false), Delays);

        // Delay elapses without emitting CloseDoors; next action is jetway retraction.
        Assert.Equal(PushbackAction.RetractJetwayStairs, TickUntilAction(sequencer, Ready(doorsOpen: false)));
    }

    [Fact]
    public void CallPushbackDisabled_StopsAtReadyForPush()
    {
        var sequencer = NewSequencer();
        _ = sequencer.Tick(Ready(callPushback: false), Delays);
        _ = sequencer.Tick(Ready(callPushback: false), Delays);
        _ = TickUntilAction(sequencer, Ready(callPushback: false)); // doors
        _ = TickUntilAction(sequencer, Ready(callPushback: false)); // jetway
        _ = TickUntilAction(sequencer, Ready(callPushback: false)); // gpu

        Assert.Equal(PushbackSequenceStep.ReadyForPush, sequencer.Step);
        var announce = sequencer.Tick(Ready(callPushback: false), Delays);
        Assert.Contains("left to the pilot", announce.Transition, StringComparison.Ordinal);
        // Announced once, then quiet.
        Assert.Null(sequencer.Tick(Ready(callPushback: false), Delays).Transition);
        Assert.Equal(PushbackSequenceStep.ReadyForPush, sequencer.Step);
    }

    [Fact]
    public void PushbackNotCallable_WaitsAtReadyForPush()
    {
        var sequencer = NewSequencer();
        _ = sequencer.Tick(Ready(), Delays);
        _ = sequencer.Tick(Ready(), Delays);
        _ = TickUntilAction(sequencer, Ready());
        _ = TickUntilAction(sequencer, Ready());
        _ = TickUntilAction(sequencer, Ready());
        Assert.Equal(PushbackSequenceStep.ReadyForPush, sequencer.Step);

        // The wait is announced exactly once (issue #40: a silent wait hid the service-id bug).
        var waiting = sequencer.Tick(Ready(pushbackCallable: false), Delays);
        Assert.Equal(PushbackAction.None, waiting.Action);
        Assert.Contains("waiting for the GSX Departure service", waiting.Transition, StringComparison.Ordinal);
        Assert.Null(sequencer.Tick(Ready(pushbackCallable: false), Delays).Transition);
        Assert.Equal(PushbackAction.CallPushback, sequencer.Tick(Ready(), Delays).Action);
    }

    // ---- Step switches + the pilot's own pushback request (owner request 2026-11-14) ----

    private static void RunToDoorsDelay(PushbackSequencer sequencer, PushbackSequencer.Inputs inputs)
    {
        _ = sequencer.Tick(inputs, Delays); // Idle → WaitingForApu
        _ = sequencer.Tick(inputs, Delays); // APU running → first enabled step
    }

    [Fact]
    public void AStepSwitchedOff_IsSkipped_WithoutItsDelay()
    {
        var sequencer = NewSequencer();
        var inputs = Ready() with { CloseDoors = false };
        RunToDoorsDelay(sequencer, inputs);

        // Doors are off: the sequence went straight to the jetway step.
        Assert.Equal(PushbackSequenceStep.JetwayStep, sequencer.Step);
        Assert.Equal(PushbackAction.RetractJetwayStairs, TickUntilAction(sequencer, inputs));
        Assert.Equal(PushbackAction.RemoveGroundEquipment, TickUntilAction(sequencer, inputs));
        Assert.Equal(PushbackAction.CallPushback, TickUntilAction(sequencer, inputs));
    }

    [Fact]
    public void JetwayAndEquipmentOff_DoorsCloseThenReadyForPush()
    {
        var sequencer = NewSequencer();
        var inputs = Ready() with { RetractJetway = false, RemoveEquipment = false };
        RunToDoorsDelay(sequencer, inputs);

        Assert.Equal(PushbackAction.CloseDoors, TickUntilAction(sequencer, inputs));
        Assert.Equal(PushbackSequenceStep.ReadyForPush, sequencer.Step);
        Assert.Equal(PushbackAction.CallPushback, sequencer.Tick(inputs, Delays).Action);
    }

    [Fact]
    public void EveryPreparationStepOff_TheApuStillGates_ThenStraightToThePush()
    {
        var sequencer = NewSequencer();
        var inputs = Ready() with { CloseDoors = false, RetractJetway = false, RemoveEquipment = false };
        _ = sequencer.Tick(inputs with { ApuRunning = false }, Delays);
        Assert.Equal(PushbackAction.None, sequencer.Tick(inputs with { ApuRunning = false }, Delays).Action);
        Assert.Equal(PushbackSequenceStep.WaitingForApu, sequencer.Step);

        var ready = sequencer.Tick(inputs, Delays);
        Assert.Equal(PushbackSequenceStep.ReadyForPush, sequencer.Step);
        Assert.Contains("ready for push", ready.Transition, StringComparison.Ordinal);
        Assert.Equal(PushbackAction.CallPushback, sequencer.Tick(inputs, Delays).Action);
    }

    [Fact]
    public void AStepSwitchedOffDuringItsDelay_DoesNotAct()
    {
        var sequencer = NewSequencer();
        RunToDoorsDelay(sequencer, Ready());
        Assert.Equal(PushbackSequenceStep.DoorsStep, sequencer.Step);

        var off = Ready() with { CloseDoors = false };
        var first = sequencer.Tick(off, Delays);
        var second = sequencer.Tick(off, Delays);
        Assert.Equal(PushbackAction.None, first.Action);
        Assert.Equal(PushbackAction.None, second.Action);
        Assert.Equal(PushbackSequenceStep.JetwayStep, sequencer.Step);
    }

    [Fact]
    public void CallPushbackOff_ThePilotsRequest_CallsTheTugAtReadyForPush()
    {
        var sequencer = NewSequencer();
        var manual = Ready(callPushback: false);
        RunToDoorsDelay(sequencer, manual);
        _ = TickUntilAction(sequencer, manual); // doors
        _ = TickUntilAction(sequencer, manual); // jetway
        _ = TickUntilAction(sequencer, manual); // gpu
        Assert.Contains("request pushback", sequencer.Tick(manual, Delays).Transition, StringComparison.Ordinal);
        Assert.Equal(PushbackAction.None, sequencer.Tick(manual, Delays).Action);

        var call = sequencer.Tick(manual with { PushbackRequested = true }, Delays);

        Assert.Equal(PushbackAction.CallPushback, call.Action);
        Assert.Contains("requested by the pilot", call.Transition, StringComparison.Ordinal);
        Assert.Equal(PushbackSequenceStep.PushbackCalled, sequencer.Step);
    }

    [Fact]
    public void CallPushbackOff_ARequestMadeEarly_IsKeptUntilTheSequenceIsReady()
    {
        var sequencer = NewSequencer();
        var early = Ready(callPushback: false) with { PushbackRequested = true };
        RunToDoorsDelay(sequencer, early);

        Assert.Equal(PushbackAction.CloseDoors, TickUntilAction(sequencer, early));
        Assert.Equal(PushbackAction.RetractJetwayStairs, TickUntilAction(sequencer, early));
        Assert.Equal(PushbackAction.RemoveGroundEquipment, TickUntilAction(sequencer, early));
        Assert.Equal(PushbackAction.CallPushback, sequencer.Tick(early, Delays).Action);
    }

    [Fact]
    public void CallPushbackOff_TheFoStillAsksAtc_AndThePushWaitsForThePilot()
    {
        var sequencer = NewSequencer();
        var manual = Ready(callPushback: false) with { RequestClearance = true };
        RunToDoorsDelay(sequencer, manual);
        _ = TickUntilAction(sequencer, manual); // doors
        _ = TickUntilAction(sequencer, manual); // jetway
        _ = TickUntilAction(sequencer, manual); // gpu

        Assert.Equal(PushbackAction.RequestPushClearance, sequencer.Tick(manual, Delays).Action);
        var approved = sequencer.Tick(manual with { Clearance = ProsimCompanion.Core.Radios.PushClearanceState.Approved }, Delays);
        Assert.Contains("stays with the pilot", approved.Transition, StringComparison.Ordinal);

        var settled = manual with { Clearance = ProsimCompanion.Core.Radios.PushClearanceState.Approved };
        Assert.Equal(PushbackAction.None, sequencer.Tick(settled, Delays).Action); // announced, waiting
        Assert.Equal(PushbackAction.CallPushback, sequencer.Tick(settled with { PushbackRequested = true }, Delays).Action);
    }

    [Fact]
    public void Reset_ReturnsToIdle()
    {
        var sequencer = NewSequencer();
        _ = sequencer.Tick(Ready(), Delays);
        sequencer.Reset();

        Assert.Equal(PushbackSequenceStep.Idle, sequencer.Step);
    }
}
