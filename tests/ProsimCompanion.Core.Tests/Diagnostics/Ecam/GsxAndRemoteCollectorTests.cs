using Microsoft.AspNetCore.Http;
using Moq;
using ProsimCompanion.Audio.Diagnostics;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Diagnostics;
using ProsimCompanion.Core.Diagnostics.Ecam;
using ProsimCompanion.Core.Diagnostics.Metrics;
using ProsimCompanion.Core.State;
using ProsimCompanion.Gsx;
using ProsimCompanion.Gsx.Diagnostics;
using ProsimCompanion.Web.Diagnostics;
using Xunit;

namespace ProsimCompanion.Core.Tests.Diagnostics.Ecam;

/// <summary>The GSX, remote-audio and web-host collectors, the circuit registry and the API
/// client middleware, each in its normal, caution and missing-data states.</summary>
public sealed class GsxAndRemoteCollectorTests
{
    private static readonly DateTimeOffset T0 = EcamTestSupport.T0;

    // ---- GSX ----

    private static (GsxDiagnosticsCollector Collector, GsxDiagnosticsStore Store, Mock<IGsxRemoteApi> Api) Gsx(GsxReadiness readiness)
    {
        var store = new GsxDiagnosticsStore();
        var api = new Mock<IGsxRemoteApi>();
        api.SetupGet(a => a.Readiness).Returns(readiness);
        var collector = new GsxDiagnosticsCollector(store, api.Object, new DoorStateReader(new TableDataRefs()), new DepartureCycleState());
        return (collector, store, api);
    }

    [Fact]
    public void Gsx_NotReady_EveryServiceIsNoData_WithTheReasonOnTheLine()
    {
        var (collector, _, _) = Gsx(GsxReadiness.ConnectedGsxNotRunning);
        var (page, cautions, _) = CollectorTestSupport.Run(collector);

        Assert.All(GsxDiagnosticsCollector.Services, s => Assert.Equal(DiagState.NoData, page.Nodes[s.Node].State));
        Assert.Equal("COUATL NOT RUNNING", page.CautionLine);
        Assert.Equal("NO COUATL", page.Readout("memo_gsx"));
        Assert.Empty(cautions.Cautions);
    }

    [Fact]
    public void Gsx_Ready_MapsStages_AndQueuedServiceRaisesTheCaution()
    {
        var (collector, store, _) = Gsx(GsxReadiness.Ready);
        store.UpdateServiceBoard(
        [
            new GsxServiceBoardRow(GsxServiceIds.OperateJetways, GsxServiceStage.Active, "door 1L"),
            new GsxServiceBoardRow(GsxServiceIds.Gpu, GsxServiceStage.Completed, null),
            new GsxServiceBoardRow(GsxServiceIds.OperateStairs, GsxServiceStage.Skipped, null),
            new GsxServiceBoardRow(GsxServiceIds.Catering, GsxServiceStage.Called, "wait 02:14"),
            new GsxServiceBoardRow(GsxServiceIds.Refueling, GsxServiceStage.Active, "7.9 / 9.2 t"),
            new GsxServiceBoardRow(GsxServiceIds.Boarding, GsxServiceStage.Waiting, null),
        ]);
        store.UpdateBoardingCounters(new GsxBoardingCountersView(174, 112, null, 50, null));

        var (page, cautions, _) = CollectorTestSupport.Run(collector);

        Assert.Equal(EcamTokens.Conn, page.Nodes["jetway"].Value);
        Assert.Equal("DOOR 1L", page.Nodes["jetway"].Detail);
        Assert.Equal(EcamTokens.Conn, page.Nodes["gpu"].Value);
        Assert.Equal(DiagState.NotApplicable, page.Nodes["stairs"].State);
        Assert.Equal(DiagState.Caution, page.Nodes["catering"].State);
        Assert.Equal(EcamTokens.Req, page.Nodes["catering"].Value);
        Assert.Equal(EcamTokens.Prog, page.Nodes["refuel"].Value);
        Assert.Equal(EcamTokens.Avail, page.Nodes["boarding"].Value);
        Assert.Equal(DiagState.NotApplicable, page.Nodes["pushback"].State);   // not on the board
        Assert.Equal("1R", page.Readout("catering_door"));   // forward unless the GSX profile says aft
        Assert.Equal(112, page.Gauges["pax"].Value);
        Assert.Equal(174, page.Gauges["pax"].Max);
        Assert.Equal(50, page.Gauges["cargo"].Value);
        Assert.False(page.Gauges.ContainsKey("fuel"));   // no refuel sync wired in this test
        Assert.Equal("CATERING QUEUED · NEXT SVC BOARDING", page.CautionLine);
        var caution = Assert.Single(cautions.Cautions);
        Assert.Equal("CATERING QUEUED", caution.Title);
        Assert.Equal("WAIT 02:14", Assert.Single(caution.Actions).Value);
        Assert.Equal("BOARDING", page.Readout("memo_smartbtn"));
    }

    [Fact]
    public void Gsx_LiveStateBeatsTheBoard_AndTheGpuBoxIsProsimTruth()
    {
        var (collector, store, _) = Gsx(GsxReadiness.Ready);
        // The board only knows catering (held) and refuel (idle); GSX itself says the jetway is
        // docked and the stand has no stairs, and ProSim says the ground power is on.
        store.Update(GsxDiagnosticsSnapshot.Empty with
        {
            Services =
            [
                new GsxServiceView(GsxServiceIds.OperateJetways, "Jetway", "performing", "Active", false, false, null, GsxServiceStage.Active),
                new GsxServiceView(GsxServiceIds.OperateStairs, "Stairs", "unavailable", "NotAvailable", false, false, null, GsxServiceStage.Skipped),
                new GsxServiceView(GsxServiceIds.Gpu, "GPU", "available", "Callable", true, false, null, GsxServiceStage.Waiting),
                new GsxServiceView(GsxServiceIds.Catering, "Catering", "available", "Callable", true, false, null, GsxServiceStage.Waiting),
                new GsxServiceView(GsxServiceIds.Refueling, "Refuel", "completed", "Completed", false, false, "9.2 t", GsxServiceStage.Completed),
            ],
        });
        store.UpdateServiceBoard(
        [
            new GsxServiceBoardRow(GsxServiceIds.Catering, GsxServiceStage.Held, "beacon on"),
            new GsxServiceBoardRow(GsxServiceIds.Refueling, GsxServiceStage.Waiting, null),
        ]);
        store.UpdateGroundPower(true);

        var (page, _, _) = CollectorTestSupport.Run(collector);

        Assert.Equal(EcamTokens.Conn, page.Nodes["jetway"].Value);            // docked, although not on the board
        Assert.Equal(DiagState.NotApplicable, page.Nodes["stairs"].State);    // GSX: no stairs at this stand
        Assert.Equal(EcamTokens.Unavail, page.Nodes["stairs"].Detail);
        Assert.Equal(EcamTokens.Conn, page.Nodes["gpu"].Value);               // ProSim external power, not GSX's "available"
        Assert.Equal("EXT PWR", page.Nodes["gpu"].Detail);
        Assert.Equal("HELD", page.Nodes["catering"].Value);                   // GSX idle → the board's reason shows
        Assert.Equal("BEACON ON", page.Nodes["catering"].Detail);
        Assert.Equal(EcamTokens.Done, page.Nodes["refuel"].Value);            // GSX finished → beats the board's "waiting"
        Assert.Equal("9.2 T", page.Nodes["refuel"].Detail);
        Assert.Equal(DiagState.NotApplicable, page.Nodes["pushback"].State);  // known to nobody → NOT REQ
        Assert.Equal(EcamTokens.NotReq, page.Nodes["pushback"].Detail);

        // Ground power off: OFF in white with GSX's own word as the detail, and the box exists
        // even when the GPU is on no board at all.
        store.UpdateGroundPower(false);
        (page, _, _) = CollectorTestSupport.Run(collector);
        Assert.Equal(EcamTokens.Off, page.Nodes["gpu"].Value);
        Assert.Equal(DiagState.NotApplicable, page.Nodes["gpu"].State);
        Assert.Equal("GSX AVAIL", page.Nodes["gpu"].Detail);
    }

    [Fact]
    public void GsxSource_OnTheSimPage_LostIsACaution()
    {
        var api = new Mock<IGsxRemoteApi>();
        api.SetupGet(a => a.Readiness).Returns(GsxReadiness.Disconnected);
        var connections = new ConnectionStatusStore();
        connections.Set(Subsystems.Gsx, ConnectionState.Disconnected);
        var (page, cautions, _) = CollectorTestSupport.Run(new GsxSourceDiagnosticsCollector(api.Object, connections));

        Assert.Equal(DiagPageIds.Sim, new GsxSourceDiagnosticsCollector(api.Object, connections).Page);
        Assert.Equal(EcamTokens.Reconn, page.Nodes["gsx"].Value);
        Assert.Single(cautions.Cautions, c => c.Title == "GSX API LOST");
        Assert.Contains("GROUND SERVICES", cautions.InopSystems);
    }

    // ---- Remote audio ----

    private static RemoteAudioDiagnosticsCollector Remote(MixerStatusStore store, params MixerMapping[] mappings)
        => new(store, new FixedOptionsMonitor<MixerOptions>(new MixerOptions { Enabled = true, Mappings = [.. mappings] }));

    [Fact]
    public void Mixer_NoAgents_NothingDrawn_NothingRaised()
    {
        var (page, cautions, _) = CollectorTestSupport.Run(Remote(new MixerStatusStore()));
        Assert.Empty(page.Nodes);
        Assert.Equal("0", page.Readout("mixer_count"));
        Assert.Empty(cautions.Cautions);
    }

    [Fact]
    public void Mixer_ConnectedAgents_AndOneLost_RaisesTheCautionWithTheMutedChannel()
    {
        var store = new MixerStatusStore();
        store.Update(s => s with
        {
            Enabled = true,
            Agents =
            [
                new MixerAgentStatus("Voicemeeter", "10.0.1.10:5088", MixerConnectionState.Connected, new MixerVoicemeeterStatus(true, "potato", "3.1"), null, T0, 0),
                new MixerAgentStatus("Mac mini", "10.0.1.30:5088", MixerConnectionState.Disconnected, MixerVoicemeeterStatus.Unknown, "refused", null, 0),
            ],
            Mappings =
            [
                new MixerMappingStatus("Voicemeeter", AcpSide.Captain, AudioChannel.Vhf1, false, 0, -12, false, null),
            ],
        });
        var collector = Remote(store,
            new MixerMapping { Acp = AcpSide.Captain, Channel = AudioChannel.Vhf1, Agent = "Voicemeeter", StripIndex = 0 },
            new MixerMapping { Acp = AcpSide.Captain, Channel = AudioChannel.Pa, Agent = "Mac mini", StripIndex = 2, IsBus = true });

        var (page, cautions, _) = CollectorTestSupport.Run(collector, T0);
        Assert.Equal(DiagState.Normal, page.Nodes["mixer_1"].State);
        Assert.Equal("POTATO 3.1", page.Nodes["mixer_1"].Detail);
        Assert.Equal("POTATO 3.1", page.Readout("mixer_1_vm"));
        Assert.Equal("1 CH", page.Readout("mixer_1_ch"));
        Assert.Equal("10.0.1.30:5088", page.Readout("mixer_2_endpoint"));
        Assert.Equal(DiagState.Caution, page.Nodes["mixer_2"].State);
        Assert.Equal("LOST 0 S", page.Nodes["mixer_2"].Detail);
        Assert.Equal(EcamTokens.On, page.Nodes["ch_1"].Value);
        Assert.Equal("-12 dB", page.Nodes["ch_1"].Detail);
        Assert.Equal("VHF1", page.Readout("ch_1_label"));
        Assert.Equal(DiagState.Caution, page.Nodes["ch_2"].State);
        Assert.Equal(EcamTokens.Muted, page.Nodes["ch_2"].Detail);
        var caution = Assert.Single(cautions.Cautions);
        Assert.Equal("MIXER AGENT 2 LOST", caution.Title);
        Assert.Equal("PA CHANNEL", Assert.Single(caution.Actions).Label);
        Assert.Contains("PA AUDIO", cautions.InopSystems);
        Assert.Equal("MIXER AGENT 2 LOST · PA MUTED", page.CautionLine);

        // The lost clock advances with the tick.
        var (later, _, _) = CollectorTestSupport.Run(collector, T0.AddSeconds(42));
        Assert.Equal("LOST 42 S", later.Nodes["mixer_2"].Detail);
    }

    // ---- Web host ----

    [Fact]
    public void WebHost_NoCircuitsNoDeck_IsNoData()
    {
        var listener = new WebListenerStatus();
        listener.Set(new WebListenerSnapshot("http://0.0.0.0:5320", null, false, null, false));
        var collector = new WebHostDiagnosticsCollector(new CircuitRegistry(), listener, new RateRegistry(), new ApiClientRegistry());
        var (page, cautions, _) = CollectorTestSupport.Run(collector);

        Assert.Equal("0 CLIENTS", page.Nodes["webhost"].Value);
        Assert.Equal("0.0.0.0:5320", page.Readout("listen"));
        Assert.Equal(EcamTokens.Off, page.Readout("tls"));
        Assert.Equal(DiagState.NoData, page.Nodes["streamdeck"].State);
        Assert.False(page.Nodes.ContainsKey("display_1"));
        Assert.Empty(cautions.Cautions);
    }

    [Fact]
    public void WebHost_CircuitsAndAFreshDeck_AreGreen_AStaleDeckIsAmber()
    {
        var circuits = new CircuitRegistry();
        circuits.Set(new CircuitView("c1", "10.0.1.5", "CHROME · DESKTOP", "/briefing", 6, true, T0.AddMinutes(-5)));
        circuits.Set(new CircuitView("c2", "10.0.1.7", "SAFARI · IPAD", "/checklists", 14, false, T0.AddMinutes(-1)));
        var listener = new WebListenerStatus();
        listener.Set(new WebListenerSnapshot("http://0.0.0.0:5320", "https://0.0.0.0:5321", true, null, false));
        var clients = new ApiClientRegistry();
        clients.Seen("10.0.1.9", "ProsimCompanion-StreamDeck/1.4.0", T0.AddSeconds(-1));
        var collector = new WebHostDiagnosticsCollector(circuits, listener, new RateRegistry(), clients);

        var (page, cautions, _) = CollectorTestSupport.Run(collector, T0);
        Assert.Equal("2 CLIENTS", page.Nodes["webhost"].Value);
        Assert.Equal("CHROME · DESKTOP", page.Nodes["display_1"].Value);
        Assert.Equal("/BRIEFING", page.Nodes["display_1"].Detail);
        Assert.Equal("6", page.Readout("display_1_ms"));
        Assert.Equal(DiagState.Caution, page.Nodes["display_2"].State);
        Assert.Equal("RECONNECTING", page.Nodes["display_2"].Detail);
        Assert.Equal("1 LOST", page.Readout("memo_clients_lost"));
        Assert.Equal(DiagState.Normal, page.Nodes["streamdeck"].State);
        Assert.Equal("1 S", page.Nodes["streamdeck"].Value);
        Assert.Equal("PLUGIN 1.4.0", page.Nodes["streamdeck"].Detail);
        Assert.Equal(EcamTokens.On, page.Readout("tls"));
        Assert.Empty(cautions.Cautions);

        var (stale, staleCautions, _) = CollectorTestSupport.Run(collector, T0.AddSeconds(30));
        Assert.Equal(DiagState.Caution, stale.Nodes["streamdeck"].State);
        Assert.Single(staleCautions.Cautions, c => c.Title == "STREAM DECK NOT POLLING");
    }

    [Fact]
    public void CircuitTracker_ClassifiesUserAgentsAndRoutes()
    {
        Assert.Equal("SAFARI · IPAD", CircuitTracker.ClassifyUserAgent("Mozilla/5.0 (iPad; CPU OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1"));
        Assert.Equal("EDGE · DESKTOP", CircuitTracker.ClassifyUserAgent("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36 Edg/130.0"));
        Assert.Equal("CHROME · PHONE", CircuitTracker.ClassifyUserAgent("Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Mobile Safari/537.36"));
        Assert.Equal("BROWSER · DESKTOP", CircuitTracker.ClassifyUserAgent(null));
        Assert.Equal("/briefing", CircuitTracker.RouteOf("http://localhost:5320/briefing?x=1"));
        Assert.Equal("/", CircuitTracker.RouteOf("http://localhost:5320"));
    }

    [Fact]
    public void ApiClientRegistry_TracksLastSeen_AndFindsTheDeckByUserAgent()
    {
        var registry = new ApiClientRegistry();
        registry.Seen("10.0.1.9", "ProsimCompanion-StreamDeck/1.4.0", T0);
        registry.Seen("10.0.1.9", "ProsimCompanion-StreamDeck/1.4.0", T0.AddSeconds(1));
        registry.Seen("10.0.1.5", "curl/8.0", T0.AddSeconds(2));

        var deck = registry.StreamDeck();
        Assert.NotNull(deck);
        Assert.Equal(2, deck.Requests);
        Assert.Equal(T0.AddSeconds(1), deck.LastSeenUtc);
        Assert.Equal(2, registry.Snapshot().Count);
        Assert.Equal("10.0.1.5", registry.Snapshot()[0].RemoteAddress);
    }

    [Fact]
    public async Task ApiClientMiddleware_CountsOnlyApiRequests()
    {
        var registry = new ApiClientRegistry();
        var rates = new RateRegistry();
        var passed = 0;
        var middleware = new ProsimCompanion.App.Hosting.ApiClientTrackingMiddleware(_ => { passed++; return Task.CompletedTask; }, registry, rates);

        var api = new DefaultHttpContext();
        api.Request.Path = "/api/status";
        api.Request.Headers.UserAgent = "ProsimCompanion-StreamDeck/1.4.0";
        api.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("10.0.1.9");
        await middleware.InvokeAsync(api);

        var page = new DefaultHttpContext();
        page.Request.Path = "/diagnostics";
        await middleware.InvokeAsync(page);

        Assert.Equal(2, passed);
        Assert.Single(registry.Snapshot());
        Assert.NotNull(registry.StreamDeck());
        Assert.True(rates[RateNames.Api].PerSecond > 0);
    }
}

public sealed class GsxCateringDoorTests
{
    [Theory]
    [InlineData("forward", "1R")]
    [InlineData("aft", "4R")]
    [InlineData("AFT", "4R")]
    [InlineData("", "1R")]
    public void CateringDoor_FollowsTheGsxDoorSetting(string setting, string expected)
    {
        var api = new Mock<IGsxRemoteApi>();
        api.SetupGet(a => a.Readiness).Returns(GsxReadiness.Ready);
        var collector = new GsxDiagnosticsCollector(
            new GsxDiagnosticsStore(), api.Object, new DoorStateReader(new TableDataRefs()), new DepartureCycleState(),
            options: new FixedOptionsMonitor<GsxOptions>(new GsxOptions { CateringDoor = setting }));

        var (page, _, _) = CollectorTestSupport.Run(collector);

        Assert.Equal(expected, page.Readout("catering_door"));
    }

    /// <summary>Auto (the default) follows what GSX taught the door service; an explicit pick
    /// beats the learned door; nothing learned yet means forward.</summary>
    [Theory]
    [InlineData("auto", null, "1R")]
    [InlineData("auto", "aft", "4R")]
    [InlineData("auto", "forward", "1R")]
    [InlineData("forward", "aft", "1R")]
    [InlineData("aft", "forward", "4R")]
    public void CateringDoor_AutoFollowsTheLearnedDoor(string setting, string? learned, string expected)
    {
        var api = new Mock<IGsxRemoteApi>();
        api.SetupGet(a => a.Readiness).Returns(GsxReadiness.Ready);
        var collector = new GsxDiagnosticsCollector(
            new GsxDiagnosticsStore(), api.Object, new DoorStateReader(new TableDataRefs()), new DepartureCycleState(),
            options: new FixedOptionsMonitor<GsxOptions>(new GsxOptions { CateringDoor = setting, CateringDoorLearned = learned }));

        var (page, _, _) = CollectorTestSupport.Run(collector);

        Assert.Equal(expected, page.Readout("catering_door"));
    }
}
