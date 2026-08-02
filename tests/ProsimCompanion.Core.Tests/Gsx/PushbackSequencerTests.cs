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

        Assert.Equal(PushbackAction.None, sequencer.Tick(Ready(pushbackCallable: false), Delays).Action);
        Assert.Equal(PushbackAction.CallPushback, sequencer.Tick(Ready(), Delays).Action);
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
