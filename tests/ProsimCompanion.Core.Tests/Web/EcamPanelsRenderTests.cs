using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Diagnostics.Ecam;
using ProsimCompanion.Core.Hardware.Skalarki;
using ProsimCompanion.Core.Tests.Diagnostics.Ecam;
using ProsimCompanion.Core.Tests.Localization;
using ProsimCompanion.Web.Components.Diagnostics;
using Xunit;

namespace ProsimCompanion.Core.Tests.Web;

/// <summary>The PANELS page and its layout file: hidden from the selector while the snapshot
/// has no PANELS page, a LOST board in the caution class, the HW CHECK list with prompts and
/// DONE lines, buttons only when a control is wired, and the shipped layout round-trips with
/// every board name valid.</summary>
public sealed class EcamPanelsRenderTests
{
    private static readonly DateTimeOffset T0 = EcamTestSupport.T0;

    private static DiagnosticsSnapshot WithPanels(Action<DiagPageBuilder, DiagCautionSink> draw)
    {
        var core = new DiagnosticsSnapshotCore(new CountingLogger());
        return core.ProcessTick(T0, [new DelegateCollector(DiagPageIds.Panels, draw)], new FixedMemoSource(), new DiagEventLog(), null);
    }

    private static DiagnosticsSnapshot Fixture() => WithPanels((p, c) =>
    {
        p.Node("profiler", DiagState.Normal, "CONN", "10.0.1.21:53000", "SDK 5.1.0");
        p.Readout("host", "10.0.1.21:53000").Readout("license", "200200").Readout("evt_s", "3.0").Readout("link_since", "09:00");
        p.Node("overhead", DiagState.Normal, "CONN", "LAST 11:04 APU");
        p.Node("glare", DiagState.Normal, "CONN", "LAST 11:09 FCU");
        p.Node("mip", DiagState.Normal, "CONN");
        p.Node("clock", DiagState.Normal, "CONN");
        p.Node("ecam", DiagState.Normal, "CONN");
        p.Node("mcdu1", DiagState.Normal, "CONN");
        p.Node("mcdu2", DiagState.Normal, "CONN");
        p.Node("rmp1", DiagState.Normal, "CONN");
        p.Node("rmp2", DiagState.Caution, "LOST", "42 S");
        p.Node("acp1", DiagState.Normal, "CONN");
        p.Node("acp2", DiagState.Normal, "CONN");
        p.Node("atc", DiagState.Normal, "CONN");
        p.Node("tq", DiagState.Normal, "CONN");
        p.Node("lower_ped", DiagState.Normal, "CONN");
        p.NotApplicable("pwr_ctrl", "OFF");
        p.Node("rmp3", DiagState.Caution, "LOST", "7 S");          // in the overhead, drawn by no row
        p.Node("acp3", DiagState.Normal, "CONN");
        MarkRestNotApplicable(p);
        p.Readout("label_rmp2", "RMP 2");
        p.Readout("check_progress", "6/14").Readout("check_glare", "DONE").Readout("check_glare_at", "11:09")
            .Readout("check_mcdu1", "PRESS CLR").Readout("check_rmp2", "LOST");
        p.Readout("memo_devices", "13/14").Readout("memo_profiler", "5.1.0").Readout("memo_license", "200200").Readout("memo_link", "LINK UP");
        p.CautionLine("RMP 2 LOST · HW RESET AVAIL");
        c.Raise("SKALARKI", "RMP 2 LOST", DiagState.Caution, new DiagAction("HW RESET", "AVAIL"));
    });

    private static async Task<string> RenderAsync<TComponent>(DiagnosticsSnapshot snap, Action<IServiceCollection>? configure = null, string? zone = null)
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
            var parameters = new Dictionary<string, object?> { ["Snap"] = snap };
            if (zone is not null)
            {
                parameters["Zone"] = zone;
            }

            var output = await renderer.RenderComponentAsync<TComponent>(ParameterView.FromDictionary(parameters));
            return System.Net.WebUtility.HtmlDecode(output.ToHtmlString());
        });
    }

    [Fact]
    public void Snapshot_WithoutPanels_HidesTheSelectorEntry_AndWithPanelsShowsIt()
    {
        Assert.DoesNotContain(DiagPageIds.Panels, DiagnosticsSnapshot.Empty.VisiblePages);
        Assert.Equal(DiagPageIds.All, Fixture().VisiblePages);
        Assert.Equal(["voice", "sim", "remote", "gsx", "panels", "status"], DiagPageIds.All);
        Assert.True(DiagPageIds.IsDrawing(DiagPageIds.Panels));
    }

    [Fact]
    public async Task Selector_OffersPanels_OnlyWhenThePageExists()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddTestLocalizer();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, NullLoggerFactory.Instance);
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<EcamSelector>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["Current"] = DiagPageIds.Voice,
                ["Pages"] = DiagnosticsSnapshot.Empty.VisiblePages,   // … the host page passes the visible ones
            }));
            return output.ToHtmlString();
        });
        Assert.DoesNotContain(">PANELS<", html, StringComparison.Ordinal);
        Assert.Contains(">STATUS<", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fixture_DrawsTheMockup_LostBoardAmber_CheckListAndMemo()
    {
        var html = await RenderAsync<EcamPanelsPage>(Fixture());

        Assert.Contains(">PANELS<", html, StringComparison.Ordinal);
        Assert.Contains("pn-profiler st-normal", html, StringComparison.Ordinal);
        Assert.Contains("10.0.1.21", html, StringComparison.Ordinal);
        Assert.Contains(">:53000<", html, StringComparison.Ordinal);
        Assert.Contains("LAST 11:04 APU", html, StringComparison.Ordinal);
        Assert.Contains("pn-box st-caution", html, StringComparison.Ordinal);            // RMP 2
        Assert.Contains("LOST 42 S", html, StringComparison.Ordinal);
        Assert.Contains("pn-box st-na", html, StringComparison.Ordinal);                 // PWR CTRL - - -
        Assert.Contains("- - -", html, StringComparison.Ordinal);
        Assert.Contains("ECAM CP", html, StringComparison.Ordinal);                      // the layout's own label
        Assert.Contains("LOWER PED", html, StringComparison.Ordinal);

        // HW CHECK: a DONE line, a prompt line, a LOST line, the progress.
        Assert.Contains("chk-state st-normal\">DONE", html, StringComparison.Ordinal);
        Assert.Contains("chk-state prompt\">PRESS CLR", html, StringComparison.Ordinal);
        Assert.Contains("chk-state st-caution\">LOST", html, StringComparison.Ordinal);
        Assert.Contains("/ 14 CHECKED", html, StringComparison.Ordinal);
        Assert.Contains("width:43%", html, StringComparison.Ordinal);                    // 6 of 14

        // No control wired: the buttons are drawn but are not <button>s, and HW RESET is not clickable.
        Assert.DoesNotContain("<button", html, StringComparison.Ordinal);
        Assert.Contains("LAMP TEST", html, StringComparison.Ordinal);
        Assert.Contains("HW RESET ....... AVAIL", html, StringComparison.Ordinal);

        // Memo: DEVICES 13 / 14 CONN in amber (short), PROFILER 5.1.0 200200, LINK UP.
        Assert.Contains(">13<", html, StringComparison.Ordinal);
        Assert.Contains("q c-amber\">/ 14 CONN", html, StringComparison.Ordinal);
        Assert.Contains(">200200<", html, StringComparison.Ordinal);
        Assert.Contains(">LINK UP<", html, StringComparison.Ordinal);
        Assert.DoesNotContain("#33ff33", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WithAControl_ButtonsAreReal_AndIdleCheckShowsPromptsInGrey()
    {
        var control = new RecordingControl();
        var snap = WithPanels((p, _) =>
        {
            p.Node("profiler", DiagState.Normal, "CONN", "10.0.1.21:53000");
            p.Node("glare", DiagState.Normal, "CONN");
            p.Node("mcdu1", DiagState.Normal, "CONN");
            MarkRestNotApplicable(p);
            p.Readout("memo_devices", "2/2").Readout("memo_link", "LINK UP");
        });
        var html = await RenderAsync<EcamPanelsPage>(snap, s =>
        {
            s.AddSingleton<ISkalarkiControl>(control);
            s.AddSingleton(new SkalarkiLayoutCatalog(null, NullLogger<SkalarkiLayoutCatalog>.Instance));
            s.AddSingleton<IOptionsMonitor<SkalarkiOptions>>(new FixedOptionsMonitor<SkalarkiOptions>(new SkalarkiOptions { Enabled = true }));
        });

        SettingsPagesRenderTests.Dump("panels-control", html);
        Assert.Contains("<button type=\"button\" class=\"abs ecam-btn\"", html, StringComparison.Ordinal);
        Assert.Contains(">START<", html, StringComparison.Ordinal);                      // no check running yet
        Assert.Contains("chk-state idle\">PRESS FCU", html, StringComparison.Ordinal);   // the layout's prompt, greyed
        Assert.Contains("chk-state idle\">PRESS CLR", html, StringComparison.Ordinal);
        Assert.Contains("Light every output", html, StringComparison.Ordinal);           // localised hover text
        // Every layout board is listed (this fixture marks none "- - -"), none checked yet.
        Assert.Contains(">0</span> <span class=\"c-cyan\">/ 2 CHECKED", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ShippedLayout_RoundTrips_AndNamesOnlyRealBoards()
    {
        var layouts = SkalarkiLayoutCatalog.Parse(SkalarkiLayoutCatalog.EmbeddedJson());
        Assert.Equal(["A32X", "A33X", "B73X", "B77X"], layouts.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList());
        foreach (var (name, layout) in layouts)
        {
            Assert.NotEmpty(layout.Rows);
            foreach (var row in layout.Rows)
            {
                var names = new[] { row.Device, row.Left, row.Right }.Concat(row.Devices ?? []).Where(n => n is not null);
                foreach (var boardName in names)
                {
                    Assert.True(SkalarkiDevices.TryParse(boardName, out _), $"{name}: '{boardName}' is not a SkalarkiDeviceId");
                }
            }

            foreach (var promptName in layout.Prompts.Keys)
            {
                Assert.True(SkalarkiDevices.TryParse(promptName, out _), $"{name}: prompt for unknown board '{promptName}'");
            }

            Assert.NotEmpty(layout.Boards());
        }

        foreach (var (name, layout) in layouts)
        {
            Assert.Equal(["OVHD", "GLARE", "MIP", "PED"], layout.Zones.Select(z => z.Id).ToList());
            foreach (var zone in layout.Zones)
            {
                Assert.Equal(zone.Boards.Count, zone.BoardIds().Count);   // every zone board is a real id
            }

            Assert.Equal(SkalarkiDeviceId.Pedestal, layout.Zone("PED")!.BoardIds()[0]);   // controller not drawn → label
            Assert.True(layout.AllBoards().Count >= 25, name);
        }

        var a32x = layouts["A32X"];
        Assert.Equal("OVHD", a32x.ZoneOf(SkalarkiDeviceId.Rmp3)!.Id);
        Assert.Equal("MIP", a32x.ZoneOf(SkalarkiDeviceId.DcduCs)!.Id);
        Assert.Null(a32x.ZoneOf(SkalarkiDeviceId.Md80Mcp));
        Assert.Equal(SkalarkiDeviceId.Overhead, a32x.Boards()[0].Device);
        Assert.Equal("ECAM CP", a32x.Boards().Single(b => b.Device == SkalarkiDeviceId.Ecam).Label);
        Assert.Equal("PRESS CLR", a32x.PromptsById()[SkalarkiDeviceId.Mcdu1]);

        // The served file and the embedded copy are the same bytes.
        // ShippedPackRoot() is src/ProsimCompanion.App/config/lang; the web root is its sibling project.
        var served = Path.GetFullPath(Path.Combine(TestLocalizer.ShippedPackRoot(), "..", "..", "..", "ProsimCompanion.Web", "wwwroot", "data", "skalarki-layout.json"));
        Assert.Equal(File.ReadAllText(served), SkalarkiLayoutCatalog.EmbeddedJson());

        var catalog = new SkalarkiLayoutCatalog(null, NullLogger<SkalarkiLayoutCatalog>.Instance);
        Assert.Same(SkalarkiLayout.Empty, catalog.For((SkalarkiCatalogue)42));
    }

    /// <summary>What the real collector does for every board the user does not watch.</summary>
    private static void MarkRestNotApplicable(DiagPageBuilder p)
    {
        foreach (var device in SkalarkiDevices.All)
        {
            var id = SkalarkiDevices.NodeId(device);
            if (!p.NodeIds.Contains(id))
            {
                p.NotApplicable(id, "OFF");
            }
        }
    }

    [Fact]
    public async Task Zones_RollUp_AndTheDrillDown_ListsEveryBoardOfTheZone()
    {
        var html = await RenderAsync<EcamPanelsPage>(Fixture());

        // OVERHEAD is the overhead zone's controller: RMP 3 (lost, drawn nowhere) rolls into
        // its bar — amber, "1 LOST", 2 of 3 connected — while its own last input stays.
        Assert.Contains("pn-box st-caution zone", html, StringComparison.Ordinal);
        Assert.Contains(">1 LOST<", html, StringComparison.Ordinal);
        Assert.Contains("pn-count\">2/3<", html, StringComparison.Ordinal);
        Assert.Contains("LAST 11:04 APU", html, StringComparison.Ordinal);
        // Every drawn box is a click target; the pedestal zone gets its label at the bottom.
        Assert.Contains("pn-small st-normal zone", html, StringComparison.Ordinal);
        Assert.Contains("pn-zone-label", html, StringComparison.Ordinal);
        Assert.Contains(">PEDESTAL</span>", html, StringComparison.Ordinal);
        // The check walks the undrawn boards too.
        Assert.Contains("RMP 3 ", html, StringComparison.Ordinal);

        var zone = await RenderAsync<EcamPanelsPage>(Fixture(), s => { }, "OVHD");
        Assert.Contains("PANELS › OVHD", zone, StringComparison.Ordinal);
        Assert.Contains(">BACK<", zone, StringComparison.Ordinal);
        Assert.Contains("zl-lbl\">RMP 3<", zone, StringComparison.Ordinal);
        Assert.Contains("zl-state\">LOST 7 S<", zone, StringComparison.Ordinal);
        Assert.Contains("zl-lbl\">ACP 3<", zone, StringComparison.Ordinal);
        Assert.Contains("zl-lbl\">REFUEL<", zone, StringComparison.Ordinal);      // unwatched boards still listed, as - - -
        Assert.DoesNotContain("head-u\" style=\"left:462px;top:48px\">HW CHECK", zone, StringComparison.Ordinal);   // the check column is replaced
        Assert.Contains("/ 3 CHECKED", zone, StringComparison.Ordinal);   // the zone's own tally: OVERHEAD, RMP 3, ACP 3 watched, none done yet
    }

    private sealed class RecordingControl : ISkalarkiControl
    {
        public Task<string> LampTestAsync(SkalarkiDeviceId? device, CancellationToken cancellationToken) => Task.FromResult("2 LIT");

        public Task<string> HardwareResetAsync(SkalarkiDeviceId device, bool hard, CancellationToken cancellationToken) => Task.FromResult("SENT");

        public void StartCheck(IReadOnlyDictionary<SkalarkiDeviceId, string> prompts)
        {
        }

        public void RestartCheck()
        {
        }

        public void StopCheck()
        {
        }
    }
}
