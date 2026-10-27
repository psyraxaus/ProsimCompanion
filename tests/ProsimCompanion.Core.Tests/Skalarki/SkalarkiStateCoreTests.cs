using ProsimCompanion.Core.Hardware.Skalarki;
using ProsimCompanion.Core.State;
using ProsimCompanion.Skalarki;
using Xunit;

namespace ProsimCompanion.Core.Tests.Skalarki;

/// <summary>The pure Skalarki transitions: link, presence with timestamps, event counting,
/// enabled filtering and the reconnect backoff — no timers, no SDK.</summary>
public sealed class SkalarkiStateCoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void LinkUp_ThenDown_ClearsPresence_AndStampsTheLoss()
    {
        var core = new SkalarkiStateCore();
        core.Configure([]);
        core.LinkUp(T0);
        core.DeviceChanged(SkalarkiDeviceId.Glare, true, T0);
        core.DeviceChanged(SkalarkiDeviceId.Rmp2, false, T0);

        var up = core.Snapshot;
        Assert.Equal(ConnectionState.Connected, up.LinkState);
        Assert.Equal(T0, up.LinkSinceUtc);
        Assert.True(up.Devices[SkalarkiDeviceId.Glare].Connected);
        Assert.True(up.Devices[SkalarkiDeviceId.Glare].Enabled);          // auto mode: connected → watched
        Assert.False(up.Devices[SkalarkiDeviceId.Rmp2].Enabled);          // never connected → not on the page
        Assert.Null(up.Devices[SkalarkiDeviceId.Rmp2].LastChangeUtc);     // never there, so not "lost"

        var down = core.LinkDown(T0.AddSeconds(30), "lost");
        Assert.Equal(ConnectionState.Disconnected, down.LinkState);
        Assert.Null(down.LinkSinceUtc);
        Assert.Equal("lost", down.Reason);
        Assert.False(down.Devices[SkalarkiDeviceId.Glare].Connected);
        Assert.True(down.Devices[SkalarkiDeviceId.Glare].Enabled);        // sticky: still drawn, as LOST
        Assert.Equal(T0.AddSeconds(30), down.Devices[SkalarkiDeviceId.Glare].LastChangeUtc);
    }

    [Fact]
    public void DeviceFlip_StampsTheChange_RepeatIsNoChange()
    {
        var core = new SkalarkiStateCore();
        core.Configure([]);
        core.LinkUp(T0);
        var first = core.DeviceChanged(SkalarkiDeviceId.Mcdu1, true, T0);
        var same = core.DeviceChanged(SkalarkiDeviceId.Mcdu1, true, T0.AddSeconds(5));
        Assert.Same(first, same);

        var lost = core.DeviceChanged(SkalarkiDeviceId.Mcdu1, false, T0.AddSeconds(42));
        Assert.False(lost.Devices[SkalarkiDeviceId.Mcdu1].Connected);
        Assert.Equal(T0.AddSeconds(42), lost.Devices[SkalarkiDeviceId.Mcdu1].LastChangeUtc);

        var back = core.DeviceChanged(SkalarkiDeviceId.Mcdu1, true, T0.AddSeconds(50));
        Assert.True(back.Devices[SkalarkiDeviceId.Mcdu1].Connected);
        Assert.Equal(T0.AddSeconds(50), back.Devices[SkalarkiDeviceId.Mcdu1].LastChangeUtc);
    }

    [Fact]
    public void ExplicitList_DrawsNamedBoardsOnly_EvenBeforeTheProfilerMentionsThem()
    {
        var core = new SkalarkiStateCore();
        core.Configure([SkalarkiDeviceId.Rmp2, SkalarkiDeviceId.Glare]);
        core.LinkUp(T0);
        core.DeviceChanged(SkalarkiDeviceId.Glare, true, T0);
        core.DeviceChanged(SkalarkiDeviceId.Overhead, true, T0);

        var snap = core.Snapshot;
        Assert.True(snap.Devices[SkalarkiDeviceId.Rmp2].Enabled);          // named, not yet reported → XX on the page
        Assert.False(snap.Devices[SkalarkiDeviceId.Rmp2].Connected);
        Assert.True(snap.Devices[SkalarkiDeviceId.Glare].Enabled);
        Assert.False(snap.Devices[SkalarkiDeviceId.Overhead].Enabled);     // connected but not wanted → settings page only
        Assert.Equal(2, snap.EnabledCount);
        Assert.Equal(1, snap.ConnectedCount);
        Assert.Equal([SkalarkiDeviceId.Glare, SkalarkiDeviceId.Rmp2], snap.EnabledDevices.Select(p => p.Key).ToList());
    }

    [Fact]
    public void HardwareEvents_CountOnTheirBoard_AndRateDecays()
    {
        var core = new SkalarkiStateCore();
        core.Configure([]);
        core.LinkUp(T0);
        core.DeviceChanged(SkalarkiDeviceId.Glare, true, T0);
        core.DeviceChanged(SkalarkiDeviceId.Overhead, false, T0);

        for (var i = 0; i < 10; i++)
        {
            core.HardwareEvent(new SkalarkiHardwareEvent(SkalarkiDeviceId.Glare, "FCU", SkalarkiSourceKind.Switch, "FCU_SPD_PUSH", "ON", T0.AddMilliseconds(100 * i)));
        }

        // A stray event from a board that is not watched is ignored.
        var ignored = core.HardwareEvent(new SkalarkiHardwareEvent(SkalarkiDeviceId.Overhead, "ELEC", SkalarkiSourceKind.Switch, "BAT1", "ON", T0.AddSeconds(1)));
        Assert.Same(core.Snapshot, ignored);

        var glare = core.Snapshot.Devices[SkalarkiDeviceId.Glare];
        Assert.Equal(10, glare.EventCount);
        Assert.Equal("FCU", glare.LastGroup);
        Assert.Equal("FCU_SPD_PUSH", glare.LastControl);
        Assert.Equal(T0.AddMilliseconds(900), glare.LastEventUtc);
        Assert.Equal(2.0, core.Snapshot.EventsPerSecond);                   // 10 in a 5 s window

        var later = core.Tick(T0.AddSeconds(3));
        Assert.Equal(2.0, later.EventsPerSecond);
        var quiet = core.Tick(T0.AddSeconds(7));
        Assert.Equal(0, quiet.EventsPerSecond);
        Assert.Same(quiet, core.Tick(T0.AddSeconds(8)));                    // nothing changed → same instance
    }

    [Fact]
    public void Backoff_DoublesPerFailure_CapsAtAMinute_ResetsOnLinkUp()
    {
        var core = new SkalarkiStateCore();
        var five = TimeSpan.FromSeconds(5);
        Assert.Equal(TimeSpan.FromSeconds(5), core.NextBackoff(five));
        Assert.Equal(TimeSpan.FromSeconds(10), core.NextBackoff(five));
        Assert.Equal(TimeSpan.FromSeconds(20), core.NextBackoff(five));
        Assert.Equal(TimeSpan.FromSeconds(40), core.NextBackoff(five));
        Assert.Equal(TimeSpan.FromSeconds(60), core.NextBackoff(five));
        Assert.Equal(TimeSpan.FromSeconds(60), core.NextBackoff(five));
        Assert.Equal(6, core.Attempt);

        core.LinkUp(T0);
        Assert.Equal(0, core.Attempt);
        Assert.Equal(TimeSpan.FromSeconds(5), core.NextBackoff(five));
    }

    [Fact]
    public void Disabled_ClearsEverythingButKeepsTheReason()
    {
        var core = new SkalarkiStateCore();
        core.Configure([]);
        core.LinkUp(T0);
        core.DeviceChanged(SkalarkiDeviceId.Tq, true, T0);
        core.SetLicense("200200");

        var off = core.Disabled("No Skalarki SDK folder is set.");
        Assert.Equal(ConnectionState.Disabled, off.LinkState);
        Assert.Equal("No Skalarki SDK folder is set.", off.Reason);
        Assert.False(off.Devices[SkalarkiDeviceId.Tq].Connected);
        Assert.Equal("200200", off.License);
    }

    [Theory]
    [InlineData(SkalarkiDeviceId.Mcdu1, "mcdu1", "MCDU1", "MCDU 1")]
    [InlineData(SkalarkiDeviceId.LowerPed, "lower_ped", "LOWERPED", "LOWER PED")]
    [InlineData(SkalarkiDeviceId.DcduCs, "dcdu_cs", "DCDUCS", "DCDU CS")]
    [InlineData(SkalarkiDeviceId.Rmp3, "rmp3", "RMP3", "RMP 3")]
    [InlineData(SkalarkiDeviceId.Glare, "glare", "GLARE", "GLARE")]
    public void DeviceNames_NodeId_SdkName_Label(SkalarkiDeviceId device, string nodeId, string sdkName, string label)
    {
        Assert.Equal(nodeId, SkalarkiDevices.NodeId(device));
        Assert.Equal(sdkName, SkalarkiDevices.SdkName(device));
        Assert.Equal(label, SkalarkiDevices.DefaultLabel(device));
        Assert.True(SkalarkiDevices.TryParse(sdkName, out var parsed));
        Assert.Equal(device, parsed);
        Assert.True(SkalarkiDevices.TryParse(nodeId, out parsed));
        Assert.Equal(device, parsed);
    }

    [Fact]
    public void DeviceIds_MirrorTheSdkValues()
    {
        Assert.Equal(1, (int)SkalarkiDeviceId.Glare);
        Assert.Equal(26, (int)SkalarkiDeviceId.Draims2);
        Assert.Equal(26, SkalarkiDevices.All.Count);
        Assert.False(SkalarkiDevices.TryParse("LAST", out _));
    }
}
