using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using Moq;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Diagnostics.Ecam;
using ProsimCompanion.Core.Tests.Diagnostics.Ecam;
using ProsimCompanion.Core.Tests.Localization;
using ProsimCompanion.Web.Components.Diagnostics;
using Xunit;

namespace ProsimCompanion.Core.Tests.Web;

/// <summary>
/// The five ECAM pages over the empty snapshot (every node XX, nothing throws) and over a
/// populated fixture (caution class on the amber node, links inherit the worse state), the
/// state → class map, the nav badge, and the /diagnostics route with its selector.
/// </summary>
public sealed class EcamPagesRenderTests
{
    private static readonly DateTimeOffset T0 = EcamTestSupport.T0;

    private static DiagnosticsSnapshot Populated()
    {
        var log = new DiagEventLog();
        var core = new DiagnosticsSnapshotCore(new CountingLogger());
        IDiagnosticsCollector[] collectors =
        [
            new DelegateCollector(DiagPageIds.Voice, (p, c) =>
            {
                p.Node("mic", DiagState.Normal, "-38 dB", "16 kHz", "SIM PC");
                p.Node("vad", DiagState.Normal, "SILENCE", "SILERO", "SIM PC");
                p.Node("stt", DiagState.Normal, "412 ms", "WHISPER", "MAC MINI");
                p.Node("llm", DiagState.Caution, "XX", "HOST DOWN", "RTX 3090");
                p.Node("tts", DiagState.Normal, "86 ms", "AF_HEART", "MAC MINI");
                p.Node("out", DiagState.Normal, "IDLE", "SPEAKERS", "SIM PC");
                p.Link("mic", "vad").Link("vad", "stt").Link("stt", "llm").Link("llm", "tts").Link("tts", "out");
                p.Gauge("e2e", 1840, 0, 5000, 2500, 4000, "MS");
                p.Readout("utt_text", "\"SET FLAPS ONE\"").Readout("intent", "COMMAND").Readout("ep_llm", "10.0.1.23:11434");
                p.CautionLine("LLM HOST SLEEPING · WOL SENT");
                c.Raise("VOICE", "LLM HOST NOT RESPONDING", DiagState.Caution, new DiagAction("WOL MAGIC PKT", "SENT 00:42"), new DiagAction("RETRY", "3 / 10"));
                c.Inop("LLM BRIEFING");
                p.Events.Push("SESSION START");
            }),
            new DelegateCollector(DiagPageIds.Sim, (p, c) =>
            {
                p.Node("prosim", DiagState.Normal, "CONN", "3.10");
                p.Node("simconnect", DiagState.Caution, "RECONN", "MSFS 2024");
                p.Node("gsx", DiagState.Normal, "CONN", "COUATL");
                p.Node("dataref_bus", DiagState.Normal, "DATAREF BUS");
                p.Node("simvar_bus", DiagState.Caution, "SIMVAR BUS");
                p.Link("prosim", "dataref_bus").Link("simconnect", "simvar_bus").Link("gsx", "simvar_bus");
                p.Node("phase", DiagState.Normal, "PREFLIGHT", "GATE B12");
                p.Readout("rate_datarefs", "20").Readout("subs_datarefs", "214").Readout("stale_datarefs", "0").Readout("sim_name", "MSFS 2024");
                p.CautionLine("SIMCONNECT LOST · RECONNECT AUTO");
                c.Raise("SIM", "SIMCONNECT LOST", DiagState.Warning, new DiagAction("RECONNECT", "AUTO"));
                c.Degraded("SIMVAR FEED");
            }),
            new DelegateCollector(DiagPageIds.Remote, (p, _) =>
            {
                p.Node("webhost", DiagState.Normal, "2 CLIENTS", "1.5 REQ/S");
                p.Node("display_1", DiagState.Normal, "CHROME · DESKTOP", "/BRIEFING");
                p.Readout("display_1_ms", "6").Readout("listen", "0.0.0.0:5320").Readout("tls", "ON").Readout("memo_clients", "2");
                p.Node("streamdeck", DiagState.Caution, "42 S", "PLUGIN 1.4.0");
                p.Node("mixer_1", DiagState.Normal, "VOICEMEETER", "POTATO 3.1");
                p.Readout("mixer_1_vm", "POTATO 3.1").Readout("mixer_1_ch", "2 CH").Readout("mixer_1_endpoint", "10.0.1.21:5088");
                p.Node("mixer_2", DiagState.Caution, "MAC MINI", "LOST 42 S");
                p.Readout("mixer_2_endpoint", "10.0.1.30:5088");
                p.Node("ch_1", DiagState.Normal, "ON", "-12 dB");
                p.Readout("ch_1_label", "VHF1").Readout("ch_1_device", "STRIP 0 · VOICEMEETER");
                p.CautionLine("MIXER AGENT 2 LOST · PA MUTED");
            }),
            new DelegateCollector(DiagPageIds.Gsx, (p, _) =>
            {
                p.Node("jetway", DiagState.Normal, "CONN", "DOOR 1L");
                p.Node("catering", DiagState.Caution, "REQ", "WAIT 02:14");
                p.NotApplicable("stairs", "NOT REQ");
                p.NotApplicable("deice", "NOT REQ");
                p.Readout("door_1l", "OPEN").Readout("door_fwd_cgo", "CLSD");
                p.Gauge("pax", 112, 0, 174, 175, 175, "PAX");
                p.Gauge("fuel", 7.9, 0, 9.2, 9.3, 9.3, "T");
                p.Readout("memo_gsx", "CONN").Readout("memo_smartbtn", "BOARDING").Readout("memo_turnaround", "00 H 23");
                p.CautionLine("CATERING QUEUED · NEXT SVC BOARDING");
            }),
        ];
        return core.ProcessTick(T0, collectors, new FixedMemoSource(), log, null);
    }

    private static async Task<string> RenderAsync<TComponent>(IDictionary<string, object?> parameters, Action<IServiceCollection>? configure = null)
        where TComponent : IComponent
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddTestLocalizer();
        configure?.Invoke(services);
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, NullLoggerFactory.Instance);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<TComponent>(ParameterView.FromDictionary(parameters));
            return System.Net.WebUtility.HtmlDecode(output.ToHtmlString());
        });
    }

    public static TheoryData<Type> Pages() =>
    [
        typeof(EcamVoicePage), typeof(EcamSimPage), typeof(EcamRemotePage), typeof(EcamGsxPage), typeof(EcamStatusPage),
    ];

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task EveryPage_RendersTheEmptySnapshot_WithAmberXX(Type page)
    {
        var html = await RenderByType(page, DiagnosticsSnapshot.Empty);

        Assert.Contains("ecam-memo", html, StringComparison.Ordinal);
        if (page != typeof(EcamStatusPage))
        {
            Assert.Contains("st-nodata", html, StringComparison.Ordinal);
            Assert.Contains(">XX<", html, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("NORMAL", html, StringComparison.Ordinal);
            Assert.Contains("NONE", html, StringComparison.Ordinal);
        }

        // No colour literal leaks into the markup: every colour comes through a class.
        Assert.DoesNotContain("#33ff33", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("#ffbf00", html, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task EveryPage_RendersThePopulatedSnapshot(Type page)
    {
        var html = await RenderByType(page, Populated());
        Assert.Contains("st-caution", html, StringComparison.Ordinal);
        Assert.Contains("st-normal", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VoicePage_CautionNodeAndItsLinks_CarryTheCautionClass()
    {
        var html = await RenderAsync<EcamVoicePage>(new Dictionary<string, object?> { ["Snap"] = Populated() });

        Assert.Contains("ecam-node-wrap st-caution", html, StringComparison.Ordinal);
        Assert.Contains("RTX 3090", html, StringComparison.Ordinal);
        Assert.Contains("LLM HOST SLEEPING · WOL SENT", html, StringComparison.Ordinal);
        // Five links: the two touching the LLM node are amber, the other three green.
        Assert.Equal(2, CountOf(html, "ecam-link st-caution"));
        Assert.Equal(3, CountOf(html, "ecam-link st-normal"));
        Assert.Contains("\"SET FLAPS ONE\"", html, StringComparison.Ordinal);
        Assert.Contains("H 05", html, StringComparison.Ordinal); // the fixture memo is 5 minutes up
    }

    [Fact]
    public async Task SimPage_BusTieIsAmber_WhenEitherBusIs()
    {
        var html = await RenderAsync<EcamSimPage>(new Dictionary<string, object?> { ["Snap"] = Populated() });
        Assert.Contains("busbar st-caution", html, StringComparison.Ordinal);
        Assert.Contains("busbar st-normal", html, StringComparison.Ordinal);
        Assert.Contains("SIMCONNECT LOST · RECONNECT AUTO", html, StringComparison.Ordinal);
        Assert.Contains("GATE B12", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GsxPage_CateringBox_FollowsTheServiceDoor()
    {
        var forward = Flat(await RenderAsync<EcamGsxPage>(new Dictionary<string, object?> { ["Snap"] = Populated() }));
        // Row 0 of the right column is y = 56; row 3 is y = 56 + 3 × 84 = 308.
        Assert.Contains("left:546px;top:56px\"><div class=\"svc-title\">CATERING", forward, StringComparison.Ordinal);

        var snap = Populated();
        var aft = snap with { Gsx = snap.Gsx with { Readouts = new Dictionary<string, string>(snap.Gsx.Readouts) { ["catering_door"] = "4R" } } };
        var html = Flat(await RenderAsync<EcamGsxPage>(new Dictionary<string, object?> { ["Snap"] = aft }));
        Assert.Contains("left:546px;top:308px\"><div class=\"svc-title\">CATERING", html, StringComparison.Ordinal);
        // BOARDING heads the left column; PUSHBACK and GPU sit above the nose. The fixture's
        // stairs are "not required", so the STAIRS box is hidden altogether.
        Assert.Contains("left:14px;top:56px\"><div class=\"svc-title\">BOARDING", html, StringComparison.Ordinal);
        Assert.DoesNotContain(">STAIRS<", html, StringComparison.Ordinal);

        // With nothing known (the empty snapshot) the stairs show as amber XX in row 2.
        var empty = Flat(await RenderAsync<EcamGsxPage>(new Dictionary<string, object?> { ["Snap"] = DiagnosticsSnapshot.Empty }));
        Assert.Contains("left:14px;top:224px\"><div class=\"svc-title\">STAIRS", empty, StringComparison.Ordinal);
        Assert.Contains("left:236px;top:8px\"><div class=\"svc-title\">PUSHBACK", html, StringComparison.Ordinal);
        Assert.Contains("left:384px;top:8px\"><div class=\"svc-title\">GPU", html, StringComparison.Ordinal);
    }

    /// <summary>Markup with the whitespace between tags removed, so an assertion can span tags.</summary>
    private static string Flat(string html) => System.Text.RegularExpressions.Regex.Replace(html, ">\\s+<", "><");

    [Fact]
    public async Task GsxPage_OpenDoorIsAmber_NotRequiredIsWhiteDashes_BarsHaveFigures()
    {
        var html = await RenderAsync<EcamGsxPage>(new Dictionary<string, object?> { ["Snap"] = Populated() });
        Assert.Contains("door open st-caution", html, StringComparison.Ordinal);
        Assert.Contains("svc-box right-side st-na", html, StringComparison.Ordinal);   // DEICE not required
        Assert.Contains("- - -", html, StringComparison.Ordinal);
        Assert.Contains("112 / 174", html, StringComparison.Ordinal);
        Assert.Contains("7.9 / 9.2", html, StringComparison.Ordinal);
        Assert.Contains("CATERING QUEUED", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusPage_ListsCautionsInopDegradedRecent_AndTheBundleLink()
    {
        var snap = Populated();
        var html = await RenderAsync<EcamStatusPage>(new Dictionary<string, object?> { ["Snap"] = snap, ["BundleHref"] = "api/diagnostics/bundle?sessions=5" });

        Assert.Contains("LLM HOST NOT RESPONDING", html, StringComparison.Ordinal);
        Assert.Contains("- WOL MAGIC PKT", html, StringComparison.Ordinal);
        Assert.Contains("SENT 00:42", html, StringComparison.Ordinal);
        Assert.Contains("LLM BRIEFING", html, StringComparison.Ordinal);
        Assert.Contains("SIMVAR FEED", html, StringComparison.Ordinal);
        Assert.Contains("href=\"api/diagnostics/bundle?sessions=5\"", html, StringComparison.Ordinal);
        Assert.Contains("grp-ttl st-warning", html, StringComparison.Ordinal); // SIMCONNECT LOST is a warning, listed first
        Assert.Contains("SESSION START", html, StringComparison.OrdinalIgnoreCase) ;
    }

    [Fact]
    public void Presentation_ClassMap_AndTextRules()
    {
        Assert.Equal("st-normal", EcamPresentation.StateClass(DiagState.Normal));
        Assert.Equal("st-caution", EcamPresentation.StateClass(DiagState.Caution));
        Assert.Equal("st-warning", EcamPresentation.StateClass(DiagState.Warning));
        Assert.Equal("st-na", EcamPresentation.StateClass(DiagState.NotApplicable));
        Assert.Equal("st-nodata", EcamPresentation.StateClass(DiagState.NoData));
        Assert.Equal(("0.8.0-", "RC.10"), EcamPresentation.VersionParts("0.8.0-rc.10"));
        Assert.Equal(("1.0.0", ""), EcamPresentation.VersionParts("1.0.0"));
        Assert.Equal(("02", "14"), EcamPresentation.UptimeParts(TimeSpan.FromMinutes(134)));
        Assert.Equal("- WOL MAGIC PKT ....... SENT 00:42", EcamPresentation.DottedAction(new DiagAction("WOL MAGIC PKT", "SENT 00:42")));

        var geometry = EcamPresentation.Gauge(new DiagGauge("e2e", 1840, 0, 5000, 2500, 4000, "MS"));
        Assert.NotNull(geometry.Needle);
        Assert.NotNull(geometry.WarningArcPath);
        Assert.Equal("1840", geometry.ValueText);
        Assert.Equal("st-normal", geometry.ValueClass);
        var empty = EcamPresentation.Gauge(new DiagGauge("gpu", null, 0, 100, 90, 100, "%"));
        Assert.Null(empty.Needle);
        Assert.Null(empty.WarningArcPath);
        Assert.Equal("XX", empty.ValueText);
    }

    [Fact]
    public async Task NavBadge_ShowsOnlyWithCautions_RedWithWarnings()
    {
        var none = await RenderAsync<EcamNavBadge>(new Dictionary<string, object?> { ["Memo"] = DiagnosticsSnapshot.Empty.Memo });
        Assert.DoesNotContain("side-badge", none, StringComparison.Ordinal);

        var amber = await RenderAsync<EcamNavBadge>(new Dictionary<string, object?> { ["Memo"] = DiagnosticsSnapshot.Empty.Memo with { CautionCount = 3 } });
        Assert.Contains("side-badge", amber, StringComparison.Ordinal);
        Assert.DoesNotContain("is-warning", amber, StringComparison.Ordinal);
        Assert.Contains(">3<", amber, StringComparison.Ordinal);

        var red = await RenderAsync<EcamNavBadge>(new Dictionary<string, object?> { ["Memo"] = DiagnosticsSnapshot.Empty.Memo with { CautionCount = 2, WarningCount = 1 } });
        Assert.Contains("side-badge is-warning", red, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "VOICE")]
    [InlineData("sim", "SIM")]
    [InlineData("status", "STATUS")]
    [InlineData("nonsense", "VOICE")]
    public async Task DiagnosticsRoute_RendersTheRequestedPage_WithTheSelector(string? page, string expectedTitle)
    {
        var store = new DiagnosticsStore();
        store.Publish(Populated());
        var html = await RenderAsync<ProsimCompanion.Web.Pages.Diagnostics>(
            new Dictionary<string, object?> { ["Page"] = page },
            services =>
            {
                services.AddSingleton(store);
                services.AddSingleton<IOptionsMonitor<DiagnosticsOptions>>(new FixedOptionsMonitor<DiagnosticsOptions>(new DiagnosticsOptions()));
                services.AddSingleton(Mock.Of<IJSRuntime>());
            });

        Assert.Contains($"ecam-title\" style=\"left:", html, StringComparison.Ordinal);
        Assert.Contains($">{expectedTitle}</div>", html, StringComparison.Ordinal);
        Assert.Contains("href=\"diagnostics/voice\"", html, StringComparison.Ordinal);
        Assert.Contains("href=\"diagnostics/status\"", html, StringComparison.Ordinal);
        Assert.Contains("sel-active", html, StringComparison.Ordinal);
        Assert.Contains("id=\"ecam-stage\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("diagnostics.pageTitle", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A320Anchors_LieInsideTheDrawing_AndOnTheRightSurfaces()
    {
        foreach (var (name, point) in A320Anchors.All)
        {
            Assert.InRange(point.X, 0, A320TopView.ViewBoxWidth);
            Assert.InRange(point.Y, 0, A320TopView.ViewBoxHeight);
        }

        // Doors sit between the fuselage lines; the refuel panel is outboard of the fuselage on
        // the right wing (the wing spans x 910–1581, y 565–997 in the trace).
        foreach (var door in new[] { A320Anchors.Door1L, A320Anchors.Door4L })
        {
            Assert.InRange(door.X, A320TopView.FuselageLeft, A320TopView.FuselageLeft + 40);
        }

        foreach (var door in new[] { A320Anchors.Door1R, A320Anchors.Door4R })
        {
            Assert.InRange(door.X, A320TopView.FuselageRight - 40, A320TopView.FuselageRight);
        }

        Assert.InRange(A320Anchors.RefuelPanel.X, 910, 1581);
        Assert.InRange(A320Anchors.RefuelPanel.Y, 565, 997);
        Assert.True(A320Anchors.NoseTip.Y < A320Anchors.GpuReceptacle.Y && A320Anchors.GpuReceptacle.Y < A320Anchors.Door1R.Y);
    }

    [Fact]
    public async Task RemotePage_OneBoxPerMixerAgent_WithVoicemeeterAndChannels()
    {
        var html = Flat(await RenderAsync<EcamRemotePage>(new Dictionary<string, object?> { ["Snap"] = Populated() }));

        Assert.Contains("agent-box st-normal", html, StringComparison.Ordinal);
        Assert.Contains("VOICEMEETER · CONN", html, StringComparison.Ordinal);
        Assert.Contains("POTATO 3.1 · 2 CH", html, StringComparison.Ordinal);
        Assert.Contains("agent-box st-caution", html, StringComparison.Ordinal);
        Assert.Contains("MAC MINI · LOST 42 S", html, StringComparison.Ordinal);
        Assert.Contains("10.0.1.30:5088", html, StringComparison.Ordinal);
        Assert.Contains("LISTEN", html, StringComparison.Ordinal);
        Assert.Contains("· TLS", html, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"abs sq", html, StringComparison.Ordinal);
    }

    private static async Task<string> RenderByType(Type page, DiagnosticsSnapshot snap)
    {
        var parameters = new Dictionary<string, object?> { ["Snap"] = snap };
        return page == typeof(EcamVoicePage) ? await RenderAsync<EcamVoicePage>(parameters)
            : page == typeof(EcamSimPage) ? await RenderAsync<EcamSimPage>(parameters)
            : page == typeof(EcamRemotePage) ? await RenderAsync<EcamRemotePage>(parameters)
            : page == typeof(EcamGsxPage) ? await RenderAsync<EcamGsxPage>(parameters)
            : await RenderAsync<EcamStatusPage>(parameters);
    }

    private static int CountOf(string html, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = html.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
