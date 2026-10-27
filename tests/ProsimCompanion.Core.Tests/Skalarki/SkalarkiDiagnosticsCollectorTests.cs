using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Diagnostics.Ecam;
using ProsimCompanion.Core.Hardware.Skalarki;
using ProsimCompanion.Core.State;
using ProsimCompanion.Core.Tests.Diagnostics.Ecam;
using ProsimCompanion.Skalarki;
using Xunit;

namespace ProsimCompanion.Core.Tests.Skalarki;

/// <summary>The PANELS collector: nothing while disabled, the Profiler and board nodes, LOST
/// ages and cautions with their INOP entries, the check readouts, the memo cells.</summary>
public sealed class SkalarkiDiagnosticsCollectorTests
{
    private static readonly DateTimeOffset T0 = EcamTestSupport.T0;

    private static (SkalarkiDiagnosticsCollector Collector, SkalarkiStatusStore Store) Make(SkalarkiOptions? options = null)
    {
        var store = new SkalarkiStatusStore();
        var collector = new SkalarkiDiagnosticsCollector(store, new FixedOptionsMonitor<SkalarkiOptions>(options ?? new SkalarkiOptions { Enabled = true, ProfilerHost = "10.0.1.21" }));
        return (collector, store);
    }

    [Fact]
    public void Disabled_DrawsNothing_SoThePageIsHidden()
    {
        var (collector, _) = Make(new SkalarkiOptions { Enabled = false });
        var (page, cautions, _) = CollectorTestSupport.Run(collector);

        Assert.Equal(DiagPageIds.Panels, collector.Page);
        Assert.Empty(page.Nodes);
        Assert.Empty(page.Readouts);
        Assert.Null(page.CautionLine);
        Assert.Empty(cautions.Cautions);

        // Through the core: the snapshot's Panels is null and the selector hides it.
        var core = new DiagnosticsSnapshotCore(new CountingLogger());
        var snap = core.ProcessTick(T0, [collector], new FixedMemoSource(), new DiagEventLog(), null);
        Assert.Null(snap.Panels);
        Assert.DoesNotContain(DiagPageIds.Panels, snap.VisiblePages);
    }

    [Fact]
    public void Connected_BoardsReadConn_WithLastEvent_AndLostBoardRaisesTheCaution()
    {
        var (collector, store) = Make();
        store.Publish(SkalarkiSnapshot.Empty with
        {
            LinkState = ConnectionState.Connected,
            LinkSinceUtc = T0.AddMinutes(-30),
            License = "200200",
            SdkVersion = "5.1.0",
            EventsPerSecond = 2.4,
            Devices = new Dictionary<SkalarkiDeviceId, SkalarkiDeviceView>
            {
                [SkalarkiDeviceId.Glare] = new(true, true, T0.AddMinutes(-30), T0.AddMinutes(-1), "FCU", "FCU_SPD_PUSH", 12),
                [SkalarkiDeviceId.Rmp2] = new(true, false, T0.AddSeconds(-42), null, null, null, 0),
                [SkalarkiDeviceId.Mcdu1] = new(true, true, T0.AddMinutes(-30), null, null, null, 0),
                [SkalarkiDeviceId.Overhead] = new(false, true, T0.AddMinutes(-30), null, null, null, 0),
                [SkalarkiDeviceId.Atc] = new(true, false, null, null, null, null, 0),
            },
        });

        var (page, cautions, _) = CollectorTestSupport.Run(collector);

        Assert.Equal(EcamTokens.Conn, page.Nodes["profiler"].Value);
        Assert.Equal("10.0.1.21:53000", page.Nodes["profiler"].Detail);
        Assert.Equal("SDK 5.1.0", page.Nodes["profiler"].Host);
        Assert.Equal("200200", page.Readout("license"));
        Assert.Equal("2.4", page.Readout("evt_s"));

        Assert.Equal(EcamTokens.Conn, page.Nodes["glare"].Value);
        Assert.StartsWith("LAST ", page.Nodes["glare"].Detail, StringComparison.Ordinal);
        Assert.EndsWith(" FCU", page.Nodes["glare"].Detail, StringComparison.Ordinal);
        Assert.Equal("12", page.Readout("evt_glare"));
        Assert.Equal(EcamTokens.Conn, page.Nodes["mcdu1"].Value);
        Assert.Null(page.Nodes["mcdu1"].Detail);

        Assert.Equal(DiagState.Caution, page.Nodes["rmp2"].State);
        Assert.Equal(EcamTokens.Lost, page.Nodes["rmp2"].Value);
        Assert.Equal("42 S", page.Nodes["rmp2"].Detail);

        Assert.Equal(DiagState.NotApplicable, page.Nodes["overhead"].State);     // reported but not watched
        Assert.Equal(DiagState.NoData, page.Nodes["atc"].State);                // watched, never reported
        Assert.Equal("NOT REPORTED", page.Nodes["atc"].Detail);
        Assert.Equal(DiagState.NotApplicable, page.Nodes["cb_ctrl"].State);     // unknown to the Profiler

        Assert.Equal("RMP 2 LOST · HW RESET AVAIL", page.CautionLine);
        var caution = Assert.Single(cautions.Cautions);
        Assert.Equal("RMP 2 LOST", caution.Title);
        Assert.Equal(("HW RESET", "AVAIL"), (caution.Actions[0].Label, caution.Actions[0].Value));
        Assert.Equal(["RADIO 2"], cautions.InopSystems);

        Assert.Equal("2/4", page.Readout("memo_devices"));
        Assert.Equal("5.1.0", page.Readout("memo_profiler"));
        Assert.Equal(EcamTokens.LinkUp, page.Readout("memo_link"));
        Assert.Equal("RMP 2", page.Readout("label_rmp2"));
    }

    [Fact]
    public void UserLabel_IsUsedEverywhere()
    {
        var options = new SkalarkiOptions { Enabled = true, DeviceLabels = { ["RMP2"] = "RMP FO" } };
        var (collector, store) = Make(options);
        store.Publish(SkalarkiSnapshot.Empty with
        {
            LinkState = ConnectionState.Connected,
            Devices = new Dictionary<SkalarkiDeviceId, SkalarkiDeviceView> { [SkalarkiDeviceId.Rmp2] = new(true, false, T0.AddSeconds(-5), null, null, null, 0) },
        });

        var (page, cautions, _) = CollectorTestSupport.Run(collector);
        Assert.Equal("RMP FO", page.Readout("label_rmp2"));
        Assert.Equal("RMP FO LOST", Assert.Single(cautions.Cautions).Title);
    }

    [Fact]
    public void LinkLost_EveryBoardIsXX_OneCaution_PanelsInop()
    {
        var (collector, store) = Make();
        store.Publish(SkalarkiSnapshot.Empty with
        {
            LinkState = ConnectionState.Disconnected,
            Reason = "The Skalarki Profiler at 10.0.1.21:53000 did not answer (refused).",
            Devices = new Dictionary<SkalarkiDeviceId, SkalarkiDeviceView> { [SkalarkiDeviceId.Glare] = new(true, false, T0, null, null, null, 0) },
        });

        var (page, cautions, _) = CollectorTestSupport.Run(collector);
        Assert.Equal(EcamTokens.Reconn, page.Nodes["profiler"].Value);
        Assert.Equal(DiagState.NoData, page.Nodes["glare"].State);
        Assert.Equal(EcamTokens.LinkDown, page.Nodes["glare"].Detail);
        Assert.Equal("SKALARKI PROFILER LINK LOST · RECONNECT AUTO", page.CautionLine);
        var caution = Assert.Single(cautions.Cautions);
        Assert.Equal("SKALARKI PROFILER LINK LOST", caution.Title);
        Assert.Equal([("RECONNECT", "AUTO"), ("HOST", "10.0.1.21:53000")], caution.Actions.Select(a => (a.Label, a.Value)).ToList());
        Assert.Equal(["SKALARKI PANELS"], cautions.InopSystems);
        Assert.Equal(EcamTokens.LinkDown, page.Readout("memo_link"));
        Assert.Null(page.Readout("evt_s"));
    }

    [Fact]
    public void SdkNotLoaded_IsItsOwnCaution()
    {
        var (collector, store) = Make();
        store.Publish(SkalarkiSnapshot.Empty with { LinkState = ConnectionState.Disabled, Reason = "No Skalarki SDK folder is set." });

        var (page, cautions, _) = CollectorTestSupport.Run(collector);
        Assert.Equal(DiagState.NoData, page.Nodes["profiler"].State);
        Assert.Equal("SKALARKI SDK NOT LOADED", page.CautionLine);
        Assert.Equal("SKALARKI SDK NOT LOADED", Assert.Single(cautions.Cautions).Title);
        Assert.Equal("NOT SET", cautions.Cautions[0].Actions[0].Value);
    }

    [Fact]
    public void CheckSession_BecomesReadouts()
    {
        var (collector, store) = Make();
        store.Publish(SkalarkiSnapshot.Empty with
        {
            LinkState = ConnectionState.Connected,
            Devices = new Dictionary<SkalarkiDeviceId, SkalarkiDeviceView>
            {
                [SkalarkiDeviceId.Glare] = new(true, true, T0, null, null, null, 0),
                [SkalarkiDeviceId.Mcdu1] = new(true, true, T0, null, null, null, 0),
                [SkalarkiDeviceId.Tq] = new(true, true, T0, null, null, null, 0),
            },
            Check = new SkalarkiCheckView(T0,
            [
                new(SkalarkiDeviceId.Glare, "PRESS FCU", SkalarkiCheckState.Done, T0.AddSeconds(10), "FCU_SPD_PUSH"),
                new(SkalarkiDeviceId.Mcdu1, "PRESS CLR", SkalarkiCheckState.Pending, null, null),
                new(SkalarkiDeviceId.Tq, "MOVE LVR", SkalarkiCheckState.Lost, null, null),
            ], 1, 3),
        });

        var (page, _, _) = CollectorTestSupport.Run(collector);
        Assert.Equal("1/3", page.Readout("check_progress"));
        Assert.Equal(EcamTokens.Done, page.Readout("check_glare"));
        Assert.NotNull(page.Readout("check_glare_at"));
        Assert.Equal("PRESS CLR", page.Readout("check_mcdu1"));
        Assert.Equal(EcamTokens.Lost, page.Readout("check_tq"));
        Assert.Null(page.Readout("check_complete"));
    }

    [Theory]
    [InlineData(5, "5 S")]
    [InlineData(99, "99 S")]
    [InlineData(100, "1 M")]
    [InlineData(5400, "90 M")]
    [InlineData(7200, "2 H")]
    public void Age_Words(int seconds, string expected)
        => Assert.Equal(expected, SkalarkiDiagnosticsCollector.Age(TimeSpan.FromSeconds(seconds)));
}
