using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;
using ProsimCompanion.Core.Tests.Localization;
using ProsimCompanion.Web.Components;
using Xunit;

namespace ProsimCompanion.Core.Tests.Web;

/// <summary>
/// Renders every section of the Voice First Officer settings page over two fixtures (the
/// defaults, and one with the provider and master switches on so the dependent fields are
/// live) and checks that no pack text id leaks into the page — the C3 field renderer looks
/// its texts up by convention, so a wrong root would otherwise show as "speechSettings.x.y".
/// With <c>PROSIM_RENDER_DUMP</c> set to a folder the HTML is written there too, which is how
/// the conversion was proven DOM-identical (field ids are random per render and are masked).
/// </summary>
public sealed partial class SettingsPagesRenderTests
{
    public static TheoryData<string, string?, string> SpeechSections()
    {
        var data = new TheoryData<string, string?, string>();
        foreach (var fixture in new[] { "defaults", "configured" })
        {
            foreach (var section in new[] { "general", "persona", "listening", "sterile", "voices", "groundCrew", "cabin", "company", "techlog", "sayIntentions", "mcdu", "briefing", "callouts", "abnormals" })
            {
                data.Add(section, null, fixture);
            }

            foreach (var tab in new[] { "kokoro", "elevenlabs", "google", "windows" })
            {
                data.Add("providers", tab, fixture);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(SpeechSections))]
    public async Task SpeechSettings_RendersEverySection_WithoutLeakingTextIds(string section, string? tab, string fixture)
    {
        var html = await RenderSpeechAsync(section, tab, fixture);

        Assert.Contains("<section", html, StringComparison.Ordinal);
        Assert.DoesNotMatch(LeakedTextId(), html);
        Dump($"speech-{section}{(tab is null ? "" : "-" + tab)}-{fixture}", html);
    }

    private static async Task<string> RenderSpeechAsync(string section, string? tab, string fixture)
    {
        var configured = fixture == "configured";
        var services = new ServiceCollection();
        Options(services, new SpeechOptions
        {
            KokoroBaseUrl = configured ? "http://10.0.1.30:8880" : "",
            ElevenLabsApiKey = configured ? "k" : "",
            GoogleKeyFilePath = configured ? @"C:\keys\google.json" : "",
            AsrBaseUrl = configured ? "http://10.0.1.31:8000" : "",
            MuteMicWhileSpeaking = !configured,
            AcpTransmitGating = configured,
            SterileEnabled = configured,
            Volume = configured ? 80 : 100,
            FoQuestions = { Enabled = configured, WhereAreWe = configured },
            RegionFacts = { Enabled = configured },
            Humanize = { Enabled = !configured },
        });
        Options(services, new BriefingOptions { LlmEnabled = configured, LlmApi = configured ? LlmApiKind.Ollama : LlmApiKind.OpenAi, MinimaCaptureEnabled = !configured });
        Options(services, new McduOptions { AllowActuation = configured });
        Options(services, new VoicesOptions());
        Options(services, new GroundCrewOptions { Enabled = !configured, RequireIntChannel = configured });
        Options(services, new CabinOptions { Enabled = !configured, CabinSecure = configured, AutoAnswerGround = configured, AmbientEvents = configured, CruiseQuery = configured });
        Options(services, new AccentOptions { Enabled = !configured });
        Options(services, new SayIntentionsOptions
        {
            Enabled = configured,
            ApiKeySource = configured ? "manual" : "auto",
            PttSource = configured ? "captainSidestick" : "none",
            CommsHandover = { Enabled = configured, HoldingPointToCaptain = !configured },
            FrequencyMonitorEnabled = !configured,
        });
        Options(services, new SopOptions { Stabilized = { Enabled = !configured, RequireThrustStabilized = configured }, PlacardAdvisory = { Enabled = !configured }, Monitoring = { FuelCheck = { Enabled = configured }, GrossErrorCheck = { Enabled = configured }, DestinationWeather = { Enabled = configured }, Readbacks = { Enabled = configured } } });
        Options(services, new PersonaOptions { Enabled = configured, StyleAdvisories = !configured });
        Options(services, new CompanyOptions { Enabled = !configured, CruiseMessages = configured });
        Options(services, new DebriefOptions { Enabled = !configured });
        Options(services, new DayOptions { Enabled = configured });
        Options(services, new TechLogOptions { Enabled = !configured });
        Options(services, new LogbookOptions());
        Options(services, new AbnormalsOptions { InteractiveDialogue = !configured });

        var devices = new Mock<IAudioDeviceCatalog>();
        devices.Setup(d => d.GetCaptureDeviceNames()).Returns(["Headset Mic"]);
        devices.Setup(d => d.GetRenderDeviceNames()).Returns(["Cockpit Speakers"]);
        services.AddSingleton(devices.Object);
        var ptt = new Mock<IPttInputCapture>();
        ptt.Setup(p => p.GetJoysticks()).Returns([]);
        services.AddSingleton(ptt.Object);
        var voices = new Mock<IVoiceCatalog>();
        voices.Setup(v => v.ListVoicesAsync(It.IsAny<string>(), It.IsAny<SpeechOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        services.AddSingleton(voices.Object);
        services.AddSingleton(Mock.Of<ITtsUsageReadout>(u => u.CharactersThisMonth(It.IsAny<string>()) == 1234));
        services.AddSingleton<LlmHealthStore>();

        var parameters = new Dictionary<string, object?> { ["Section"] = section, ["Tab"] = tab };
        return await RenderPageAsync<ProsimCompanion.Web.Pages.SpeechSettings>(services, "settings/speech/" + section, parameters);
    }

    internal static void Options<TOptions>(IServiceCollection services, TOptions value)
        where TOptions : class
        => services.AddSingleton<IOptionsMonitor<TOptions>>(new FixedOptionsMonitor<TOptions>(value));

    /// <summary>Renders a settings page with the shared services every one of them needs:
    /// the localizer stack over the shipped packs, "Show advanced settings" ON so the folded
    /// fields render too, a navigation manager and null loggers.</summary>
    internal static async Task<string> RenderPageAsync<TPage>(IServiceCollection services, string path, IDictionary<string, object?> parameters)
        where TPage : IComponent
    {
        services.AddSingleton<IOptionsMonitor<WebUiOptions>>(new FixedOptionsMonitor<WebUiOptions>(new WebUiOptions { Language = "en", ShowAdvancedSettings = true }));
        services.AddTestLocalizer();
        services.AddSingleton<AdvancedSettingsStore>();
        services.AddSingleton<NavigationManager>(new TestNavigationManager("http://localhost/" + path));
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, NullLoggerFactory.Instance);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<TPage>(ParameterView.FromDictionary(parameters));
            return output.ToHtmlString();
        });
    }

    /// <summary>Writes the HTML for a before/after comparison when a folder is named; the
    /// per-render field ids are masked so two renders of the same page compare equal.</summary>
    internal static void Dump(string name, string html)
    {
        var folder = Environment.GetEnvironmentVariable("PROSIM_RENDER_DUMP");
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, name + ".html"), FieldId().Replace(html, "fXXXX"));
    }

    // A pack id rendered as text: "speechSettings.general.seat.label" between tags or in an attribute.
    [GeneratedRegex(@"[>""](speechSettings|gsxSettings|audioSettings|setup|appSettings|flightPhase|notifications|fields|voicePicker)\.[a-zA-Z0-9]+(\.[a-zA-Z0-9]+)+[<""]")]
    private static partial Regex LeakedTextId();

    [GeneratedRegex("f[0-9a-f]{32}")]
    private static partial Regex FieldId();

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager(string uri) => Initialize("http://localhost/", uri);

        protected override void NavigateToCore(string uri, bool forceLoad) => Uri = ToAbsoluteUri(uri).ToString();
    }
}
