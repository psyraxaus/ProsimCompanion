using ProsimCompanion.Core.Diagnostics.Ecam.HardwareCheck;
using ProsimCompanion.Core.Hardware.Skalarki;
using Xunit;

namespace ProsimCompanion.Core.Tests.Skalarki;

/// <summary>The pure hardware-check session: a full walk to complete, a board lost mid-check
/// going LOST and back to PENDING on return, skipped boards, and RESTART.</summary>
public sealed class HardwareCheckSessionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 27, 10, 0, 0, TimeSpan.Zero);

    private static readonly Dictionary<SkalarkiDeviceId, string> Prompts = new()
    {
        [SkalarkiDeviceId.Mcdu1] = "press clr",
        [SkalarkiDeviceId.Tq] = "MOVE LVR",
    };

    private static Dictionary<SkalarkiDeviceId, SkalarkiDeviceView> Presence(params (SkalarkiDeviceId Device, bool Connected)[] boards)
        => boards.ToDictionary(b => b.Device, b => new SkalarkiDeviceView(true, b.Connected, null, null, null, null, 0));

    [Fact]
    public void FullSession_ProgressesToComplete_WithPromptsFromTheTable()
    {
        var session = new HardwareCheckSession();
        Assert.Null(session.View);

        session.Start([SkalarkiDeviceId.Glare, SkalarkiDeviceId.Mcdu1, SkalarkiDeviceId.Tq], Prompts,
            Presence((SkalarkiDeviceId.Glare, true), (SkalarkiDeviceId.Mcdu1, true), (SkalarkiDeviceId.Tq, true)), T0);

        var view = session.View!;
        Assert.Equal(T0, view.StartedUtc);
        Assert.Equal(3, view.Total);
        Assert.Equal(0, view.Done);
        Assert.False(view.Complete);
        Assert.Equal("ANY KEY", view.Items[0].Prompt);        // no table entry → default
        Assert.Equal("PRESS CLR", view.Items[1].Prompt);      // upper-cased
        Assert.Equal("MOVE LVR", view.Items[2].Prompt);
        Assert.All(view.Items, i => Assert.Equal(SkalarkiCheckState.Pending, i.State));

        Assert.True(session.OnDeviceEvent(SkalarkiDeviceId.Mcdu1, "MCDU1_CLR", T0.AddSeconds(5)));
        Assert.False(session.OnDeviceEvent(SkalarkiDeviceId.Mcdu1, "MCDU1_CLR", T0.AddSeconds(6)));   // second press: no change
        Assert.False(session.OnDeviceEvent(SkalarkiDeviceId.Rmp3, "RMP3_VHF1", T0.AddSeconds(7)));    // not in the check
        Assert.True(session.OnDeviceEvent(SkalarkiDeviceId.Glare, "FCU_SPD_PUSH", T0.AddSeconds(8)));
        Assert.Equal(2, session.View!.Done);
        Assert.False(session.View.Complete);

        Assert.True(session.OnDeviceEvent(SkalarkiDeviceId.Tq, "TQ_REV1", T0.AddSeconds(9)));
        view = session.View!;
        Assert.Equal(3, view.Done);
        Assert.True(view.Complete);
        var mcdu = view.Items.Single(i => i.Device == SkalarkiDeviceId.Mcdu1);
        Assert.Equal(SkalarkiCheckState.Done, mcdu.State);
        Assert.Equal(T0.AddSeconds(5), mcdu.DoneAtUtc);
        Assert.Equal("MCDU1_CLR", mcdu.Control);
    }

    [Fact]
    public void LostBoard_TurnsLost_AndGoesBackToPendingWhenItReturns()
    {
        var session = new HardwareCheckSession();
        session.Start([SkalarkiDeviceId.Glare, SkalarkiDeviceId.Rmp1], Prompts,
            Presence((SkalarkiDeviceId.Glare, true), (SkalarkiDeviceId.Rmp1, true)), T0);
        session.OnDeviceEvent(SkalarkiDeviceId.Rmp1, "RMP1_VHF1", T0.AddSeconds(2));
        Assert.Equal(1, session.View!.Done);

        Assert.True(session.OnDeviceLost(SkalarkiDeviceId.Rmp1, T0.AddSeconds(10)));
        var lost = session.View!.Items.Single(i => i.Device == SkalarkiDeviceId.Rmp1);
        Assert.Equal(SkalarkiCheckState.Lost, lost.State);
        Assert.Null(lost.DoneAtUtc);
        Assert.Null(lost.Control);
        Assert.Equal(0, session.View.Done);                 // the proof went with the board
        Assert.Equal(2, session.View.Total);                // still counted: it has to be touched again
        Assert.False(session.OnDeviceLost(SkalarkiDeviceId.Rmp1, T0.AddSeconds(11)));   // already lost

        Assert.True(session.OnDeviceBack(SkalarkiDeviceId.Rmp1));
        Assert.Equal(SkalarkiCheckState.Pending, session.View!.Items.Single(i => i.Device == SkalarkiDeviceId.Rmp1).State);
        Assert.False(session.OnDeviceBack(SkalarkiDeviceId.Glare));                     // was never lost

        // Events while LOST do not count; after the return they do.
        session.OnDeviceLost(SkalarkiDeviceId.Rmp1, T0.AddSeconds(20));
        Assert.False(session.OnDeviceEvent(SkalarkiDeviceId.Rmp1, "RMP1_VHF2", T0.AddSeconds(21)));
        session.OnDeviceBack(SkalarkiDeviceId.Rmp1);
        Assert.True(session.OnDeviceEvent(SkalarkiDeviceId.Rmp1, "RMP1_VHF2", T0.AddSeconds(22)));
    }

    [Fact]
    public void BoardNotConnectedAtStart_IsSkipped_NotCounted_AndJoinsWhenPluggedIn()
    {
        var session = new HardwareCheckSession();
        session.Start([SkalarkiDeviceId.Glare, SkalarkiDeviceId.Rmp2], Prompts,
            Presence((SkalarkiDeviceId.Glare, true), (SkalarkiDeviceId.Rmp2, false)), T0);

        Assert.Equal(1, session.View!.Total);
        Assert.Equal(SkalarkiCheckState.Skipped, session.View.Items[1].State);
        Assert.False(session.OnDeviceLost(SkalarkiDeviceId.Rmp2, T0.AddSeconds(1)));     // skipped stays skipped

        session.OnDeviceEvent(SkalarkiDeviceId.Glare, "FCU_ALT_PUSH", T0.AddSeconds(3));
        Assert.True(session.View!.Complete);

        Assert.True(session.OnDeviceBack(SkalarkiDeviceId.Rmp2));
        Assert.Equal(2, session.View!.Total);
        Assert.False(session.View.Complete);
    }

    [Fact]
    public void Restart_ClearsEveryLine_KeepsTheBoardList_AndStop_EndsTheCheck()
    {
        var session = new HardwareCheckSession();
        session.Start([SkalarkiDeviceId.Glare, SkalarkiDeviceId.Mcdu1], Prompts,
            Presence((SkalarkiDeviceId.Glare, true), (SkalarkiDeviceId.Mcdu1, true)), T0);
        session.OnDeviceEvent(SkalarkiDeviceId.Glare, "FCU_SPD_PUSH", T0.AddSeconds(1));
        session.OnDeviceEvent(SkalarkiDeviceId.Mcdu1, "MCDU1_CLR", T0.AddSeconds(2));
        Assert.True(session.View!.Complete);

        session.Restart(Presence((SkalarkiDeviceId.Glare, true), (SkalarkiDeviceId.Mcdu1, false)), T0.AddMinutes(1));
        var view = session.View!;
        Assert.Equal(T0.AddMinutes(1), view.StartedUtc);
        Assert.Equal(2, view.Items.Count);
        Assert.Equal(0, view.Done);
        Assert.Equal(SkalarkiCheckState.Pending, view.Items[0].State);
        Assert.Equal(SkalarkiCheckState.Skipped, view.Items[1].State);   // unplugged since
        Assert.Equal("PRESS CLR", view.Items[1].Prompt);                // prompts kept

        session.Stop();
        Assert.Null(session.View);
        Assert.False(session.OnDeviceEvent(SkalarkiDeviceId.Glare, "FCU_SPD_PUSH", T0.AddMinutes(2)));
    }
}
