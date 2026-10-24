using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Notifications;
using ProsimCompanion.Core.Profiles;
using ProsimCompanion.Core.State;
using ProsimCompanion.Core.Tests.Speech;
using Xunit;

namespace ProsimCompanion.Core.Tests.Web;

/// <summary>The other settings pages, rendered the same way (see the Speech half).</summary>
public sealed partial class SettingsPagesRenderTests
{
    public static TheoryData<string, string> GsxSections()
    {
        var data = new TheoryData<string, string>();
        foreach (var fixture in new[] { "defaults", "configured" })
        {
            foreach (var section in new[] { "doors", "equipment", "departure", "refuel", "pushback", "arrival", "operators", "questions", "connection" })
            {
                data.Add(section, fixture);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(GsxSections))]
    public async Task GsxSettings_RendersEverySection_WithoutLeakingTextIds(string section, string fixture)
    {
        var configured = fixture == "configured";
        var services = new ServiceCollection();
        Options(services, new GsxOptions
        {
            DoorAutomationEnabled = !configured,
            DoorOpenOnBoardingActive = configured,
            AutoGroundEquipment = !configured,
            SmartButtonEnabled = !configured,
            GroundPrepActivation = configured ? GsxOptions.GroundPrepActivationVoiceThenAuto : "auto",
            RefuelSyncEnabled = !configured,
            RefuelMethod = configured ? "dynamicRate" : "fixedRate",
            RandomizePaxNoShows = configured,
            PushbackPreference = configured ? "ask" : "auto",
            BeaconPushbackSequenceEnabled = !configured,
            ResolveGatesFromProfiles = !configured,
            FuelSaveLoadFob = !configured,
            AnswerCrewQuestions = !configured,
            AutoDeIce = configured,
            Deice = { AutoRequest = configured ? GsxDeiceOptions.AutoRequestAsk : GsxDeiceOptions.AutoRequestOff, OatThresholdC = configured ? 12 : 3 },
            PcaMode = configured ? "onlyJetway" : "",
            AutoPca = configured,
        });
        AddProfileMirror(services);
        services.AddSingleton<SwitchCatalogStore>();

        var html = await RenderPageAsync<ProsimCompanion.Web.Pages.GsxSettings>(services, "settings/gsx/" + section, new Dictionary<string, object?> { ["Section"] = section });

        Assert.Contains("<section", html, StringComparison.Ordinal);
        Assert.DoesNotMatch(LeakedTextId(), html);
        Dump($"gsx-{section}-{fixture}", html);
    }

    [Theory]
    [InlineData("general", "defaults")]
    [InlineData("general", "configured")]
    [InlineData("coreaudio", "defaults")]
    [InlineData("voicemeeter", "defaults")]
    [InlineData("mixer", "defaults")]
    [InlineData("mixer", "configured")]
    [InlineData("mixer", "legacy")]
    [InlineData("advanced", "defaults")]
    public async Task AudioSettings_RendersEverySection_WithoutLeakingTextIds(string section, string fixture)
    {
        var configured = fixture == "configured";
        var legacy = fixture == "legacy";
        var services = new ServiceCollection();
        Options(services, new AudioOptions { Backend = configured ? AudioBackend.VoiceMeeter : AudioBackend.CoreAudio, CoreAudioAcp = configured ? AcpSide.Observer : AcpSide.Captain });
        // "configured": two agents (#178) with a shared target (#179) so the warning renders;
        // "legacy": the pre-#178 flat host, lifted into agent block 1 on the page's first visit.
        Options(services, new MixerOptions
        {
            Enabled = configured || legacy,
            Host = legacy ? "10.0.1.40" : "",
            PanelStrips = legacy ? [0, 2] : [],
            Agents = configured
                ? [new MixerAgentOptions { Name = "PC1", Host = "10.0.1.40", PanelStrips = [0, 2] }, new MixerAgentOptions { Name = "PC2", Host = "10.0.1.41", Enabled = false }]
                : [],
            Mappings = configured
                ? [new MixerMapping { Acp = AcpSide.Captain, Channel = AudioChannel.Vhf1, Agent = "PC1", IsBus = true, StripIndex = 1 }, new MixerMapping { Acp = AcpSide.FirstOfficer, Channel = AudioChannel.Vhf1, Agent = "PC1", IsBus = true, StripIndex = 1 }]
                : [],
        });
        services.AddSingleton(Mock.Of<IAudioControl>(a => a.GetVoiceMeeterTargets() == new List<VoiceMeeterTargetView>()));
        var devices = new Mock<IAudioDeviceCatalog>();
        devices.Setup(d => d.GetRenderDeviceNames()).Returns(["Cockpit Speakers"]);
        services.AddSingleton(devices.Object);
        var mixers = new Mock<IMixerClients>();
        mixers.SetupGet(m => m.All).Returns([]);
        services.AddSingleton(mixers.Object);
        services.AddSingleton<MixerStatusStore>();

        var html = await RenderPageAsync<ProsimCompanion.Web.Pages.AudioSettings>(services, "settings/audio/" + section, new Dictionary<string, object?> { ["Section"] = section });

        Assert.Contains("<section", html, StringComparison.Ordinal);
        Assert.DoesNotMatch(LeakedTextId(), html);
        Dump($"audio-{section}-{fixture}", html);
    }

    [Theory]
    [InlineData("defaults")]
    [InlineData("configured")]
    public async Task SetupSettings_Renders_WithoutLeakingTextIds(string fixture)
    {
        var configured = fixture == "configured";
        var services = new ServiceCollection();
        Options(services, new GsxOptions { Enabled = !configured });
        Options(services, new SpeechOptions());
        Options(services, new AudioOptions());
        Options(services, new ProsimOptions { ApiKey = configured ? "secret" : null, SetupCheckEnabled = !configured });
        Options(services, new BriefingOptions());
        Options(services, new CommandApiOptions { Enabled = configured });
        Options(services, new WeatherOptions { UseActiveSkyApi = !configured });
        Options(services, new UpdateCheckOptions { Enabled = !configured });
        Options(services, new WakeTargetsOptions { Targets = configured ? [new WakeTarget { Name = "LLM box", MacAddress = "AA:BB:CC:DD:EE:FF" }, new WakeTarget { Enabled = false }] : [] });
        services.AddSingleton(Mock.Of<IWakeOnLanControl>());
        // vJoy relay card (2026-10-24): rows + the status pill in a non-Off state.
        Options(services, new InputRelayOptions { Enabled = configured, Views = configured ? [new InputViewButton { Name = "Wing", Button = 5 }, new InputViewButton()] : [] });
        services.AddSingleton(Mock.Of<IInputRelay>(relay => relay.Snapshot() == (configured
            ? new InputRelaySnapshot(InputRelayState.Ready, "vJoy device 1 acquired — 32 buttons.", 32, DateTimeOffset.UtcNow, "Wing")
            : InputRelaySnapshot.Off)));
        AddProfileMirror(services);
        services.AddSingleton<WebListenerStatus>();
        services.AddSingleton<ProsimSetupStore>();
        services.AddSingleton(Mock.Of<IProsimSetupCheck>());
        services.AddSingleton<ConnectionStatusStore>();

        var html = await RenderPageAsync<ProsimCompanion.Web.Pages.SetupSettings>(services, "settings", new Dictionary<string, object?>());

        Assert.Contains("<section", html, StringComparison.Ordinal);
        Assert.DoesNotMatch(LeakedTextId(), html);
        Dump($"setup-{fixture}", html);
    }

    [Theory]
    [InlineData("defaults")]
    [InlineData("configured")]
    public async Task AppSettings_Renders_WithoutLeakingTextIds(string fixture)
    {
        var configured = fixture == "configured";
        var services = new ServiceCollection();
        Options(services, new FlightDataOptions { AutoPrelimAtStd = configured, AutoFinalOnBoardingComplete = !configured });
        Options(services, new ChecklistOptions());
        Options(services, new FlightStatusOptions { TileOrder = configured ? ["gsx", "hero"] : [] });
        Options(services, new FlightStateOptions { SnapshotEnabled = !configured });
        Options(services, new AircraftLimitsOptions { MaxCrosswindWetKt = configured ? 30 : 33 });
        Options(services, new FuelOptions { DecisionAdvice = configured });
        Options(services, new FlightOptions { DiversionEnabled = configured });

        var html = await RenderPageAsync<ProsimCompanion.Web.Pages.AppSettings>(services, "settings/app", new Dictionary<string, object?>());

        Assert.Contains("<section", html, StringComparison.Ordinal);
        Assert.DoesNotMatch(LeakedTextId(), html);
        Dump($"app-{fixture}", html);
    }

    [Theory]
    [InlineData("defaults")]
    [InlineData("configured")]
    public async Task CockpitSettings_Renders_WithoutLeakingTextIds(string fixture)
    {
        var configured = fixture == "configured";
        var services = new ServiceCollection();
        Options(services, new CockpitSwitchesOptions
        {
            Enabled = configured,
            Rows = configured
                ?
                [
                    new CockpitSwitchRow { Id = "oh_hyd_lmv_blue", Name = "LEAK MEAS B", DataRef = "system.switches.S_OH_HYD_LMV_BLUE", LowerIndicator = "system.indicators.I_OH_HYD_LMV_BLUE_L", BezelColor = "#5fb4ff" },
                    new CockpitSwitchRow { Id = "oh_adirs_1", Name = "ADIRS 1", DataRef = "system.switches.S_OH_ADIRS_1", Look = CockpitSwitchLook.Knob, PressMode = CockpitPressMode.Cycle },
                    new CockpitSwitchRow { Id = "ghost", Name = "Ghost", DataRef = "system.switches.S_NOT_IN_LIST" },
                    new CockpitSwitchRow(),
                ]
                : [],
        });
        services.AddSingleton<SwitchCatalogStore>();
        var states = new CockpitSwitchStateStore();
        if (configured)
        {
            states.Update(_ => new CockpitSwitchesSnapshot(
            [
                new CockpitSwitchState("oh_hyd_lmv_blue", 1, "Pushed", false, true, false),
                new CockpitSwitchState("oh_adirs_1", null, null, false, false, true),
            ]));
        }

        services.AddSingleton(states);
        services.AddSingleton(Mock.Of<ICockpitSwitchControl>());

        var html = await RenderPageAsync<ProsimCompanion.Web.Pages.CockpitSettings>(services, "settings/cockpit", new Dictionary<string, object?>());

        Assert.Contains("<section", html, StringComparison.Ordinal);
        Assert.DoesNotMatch(LeakedTextId(), html);
        if (configured)
        {
            Assert.Contains("LEAK MEAS B", html, StringComparison.Ordinal);
            Assert.Contains("PUSHED", html, StringComparison.Ordinal);
        }

        Dump($"cockpit-{fixture}", html);
    }

    [Fact]
    public async Task FlightPhaseSettings_Renders_WithoutLeakingTextIds()
    {
        var services = new ServiceCollection();
        Options(services, new FlightStateOptions { TaxiSpeedMaxKt = 50 });

        var html = await RenderPageAsync<ProsimCompanion.Web.Pages.FlightPhaseSettings>(services, "settings/flight-phase", new Dictionary<string, object?>());

        Assert.Contains("<section", html, StringComparison.Ordinal);
        Assert.DoesNotMatch(LeakedTextId(), html);
        Dump("flightPhase", html);
    }

    [Theory]
    [InlineData("defaults")]
    [InlineData("configured")]
    public async Task NotificationSettings_Renders_WithoutLeakingTextIds(string fixture)
    {
        var configured = fixture == "configured";
        var options = new NotificationOptions
        {
            Enabled = configured,
            Targets = configured
                ? [new NotificationTarget { Name = "My phone", Kind = NotificationTargetKind.Ntfy, Url = "https://ntfy.sh/x" }, new NotificationTarget { Kind = NotificationTargetKind.Discord }]
                : [],
        };
        var services = new ServiceCollection();
        Options(services, options);
        var sessions = Path.Combine(Path.GetTempPath(), "ProsimCompanionTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sessions);
        services.AddSingleton(new NotificationDispatcher(new FixedOptionsMonitor<NotificationOptions>(options), new JsonlEventLog(sessions, NullLogger<JsonlEventLog>.Instance), NullLogger<NotificationDispatcher>.Instance));
        services.AddSingleton<OfpStore>();

        var html = await RenderPageAsync<ProsimCompanion.Web.Pages.NotificationSettings>(services, "settings/notifications", new Dictionary<string, object?>());

        Assert.Contains("<section", html, StringComparison.Ordinal);
        Assert.DoesNotMatch(LeakedTextId(), html);
        Dump($"notifications-{fixture}", html);
    }

    private static void AddProfileMirror(IServiceCollection services)
    {
        var settingsFile = Path.Combine(Path.GetTempPath(), "ProsimCompanionTests", Guid.NewGuid().ToString("N"), "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settingsFile)!);
        var file = new JsonSettingsFile(settingsFile);
        services.AddSingleton(file);
        services.AddSingleton(new SettingsWriter(file));
        var profiles = new AircraftProfileService(new FakeDataRefs(), new FixedOptionsMonitor<AircraftProfilesOptions>(new AircraftProfilesOptions()), new OfpStore(), NullLogger<AircraftProfileService>.Instance);
        services.AddSingleton(new ProfileGsxMirror(profiles, file, NullLogger<ProfileGsxMirror>.Instance));
    }
}
