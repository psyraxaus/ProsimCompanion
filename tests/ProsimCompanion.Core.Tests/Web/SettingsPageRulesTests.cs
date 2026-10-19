using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;
using ProsimCompanion.Web.Settings;
using Xunit;

namespace ProsimCompanion.Core.Tests.Web;

/// <summary>The page-level rules that left the settings pages' @code blocks as plain C# (C3).</summary>
public sealed class SettingsPageRulesTests
{
    [Theory]
    [InlineData("kokoro:af_heart", "", "af_heart", "")]
    [InlineData("kokoro:af_heart", "bm_george", "bm_george", "")]
    [InlineData("google:en-AU-Chirp3-HD-Aoede", "", "", "en-AU-Chirp3-HD-Aoede")]
    [InlineData("af_heart", "", "", "")]
    [InlineData("", "", "", "")]
    public void Speech_FoVoice_FillsItsProvidersEmptyFallback(string fo, string kokoro, string expectedKokoro, string expectedGoogle)
    {
        var speech = new SpeechOptions { FoVoice = fo, KokoroVoice = kokoro, GoogleVoice = "" };

        SpeechPageRules.FillProviderFallbackFromFoVoice(speech);

        Assert.Equal(expectedKokoro, speech.KokoroVoice);
        Assert.Equal(expectedGoogle, speech.GoogleVoice);
    }

    [Fact]
    public void Speech_BeforeSave_BlanksTheLegacyPttFields_AndKeepsGateNamesUnique()
    {
        var speech = new SpeechOptions { PttKey = "F12", PttJoystickDevice = 1, PttJoystickButton = 3, AtcMuteKey = "F11", AtcMuteJoystickDeviceName = "Stick" };
        var sop = new SopOptions { ApproachGates = [new ApproachGate { Name = "", AglFt = 1000 }, new ApproachGate { Name = "1000", AglFt = 1500 }] };

        SpeechPageRules.BeforeSave(speech, sop);

        Assert.Equal("", speech.PttKey);
        Assert.Null(speech.PttJoystickDevice);
        Assert.Null(speech.PttJoystickButton);
        Assert.Equal("", speech.AtcMuteKey);
        Assert.Equal("", speech.AtcMuteJoystickDeviceName);
        Assert.Equal(["1000", "1500"], sop.ApproachGates.Select(g => g.Name));
    }

    [Theory]
    [InlineData("captainSidestick", "none", "captainSidestick")]
    [InlineData("captainSidestick", "foHandMic", "foHandMic")]
    [InlineData("none", "none", "none")]
    public void Speech_PickingTheSiPttSwitch_SetsTheAtcMuteWhileThatIsOff(string siPtt, string atcMute, string expected)
    {
        var sayIntentions = new SayIntentionsOptions { PttSource = siPtt };
        var speech = new SpeechOptions { AtcMuteSource = atcMute };

        SpeechPageRules.CoupleAtcMuteToSiPtt(sayIntentions, speech);

        Assert.Equal(expected, speech.AtcMuteSource);
    }

    [Fact]
    public void Speech_MigrateLegacyBindings_JoystickWinsOverKey_AndNothingHappensOnceBound()
    {
        var speech = new SpeechOptions { PttKey = "F12", PttJoystickDevice = 2, PttJoystickButton = 5, PttJoystickDeviceName = "VIRPIL", AtcMuteKey = " RightCtrl " };

        SpeechPageRules.MigrateLegacyBindings(speech);

        Assert.Equal("joystickButton", speech.PttBinding.Kind);
        Assert.Equal(5, speech.PttBinding.Button);
        Assert.Equal("VIRPIL", speech.PttBinding.JoystickDeviceName);
        Assert.Equal("keyboard", speech.AtcMuteBinding.Kind);
        Assert.Equal("RightCtrl", speech.AtcMuteBinding.Key);
        Assert.True(SpeechPageRules.IsJoystick(speech.PttBinding));
        Assert.False(SpeechPageRules.IsJoystick(speech.AtcMuteBinding));

        speech.PttKey = "Space";
        SpeechPageRules.MigrateLegacyBindings(speech);
        Assert.Equal("joystickButton", speech.PttBinding.Kind);
    }

    [Fact]
    public void Speech_DeviceChoices_KeepAnUndetectedSavedDeviceSelectable()
    {
        var rows = SpeechPageRules.DeviceChoices("System default", ["Speakers"], "Old Headset", name => name + " (not detected)");

        Assert.Equal([("", "System default"), ("Speakers", "Speakers"), ("Old Headset", "Old Headset (not detected)")], rows);
        Assert.Equal(2, SpeechPageRules.DeviceChoices("System default", ["Speakers"], "speakers", _ => "x").Count);
    }

    [Fact]
    public void Speech_VoiceGroups_PutEnglishRegionsFirst_AndSkipSilentProviders()
    {
        var lists = new Dictionary<string, IReadOnlyList<VoiceEntry>>(StringComparer.OrdinalIgnoreCase)
        {
            ["kokoro"] =
            [
                new VoiceEntry("kokoro", "ff_siwis", "Siwis", "French", "female", null, ""),
                new VoiceEntry("kokoro", "bm_george", "George", "British English", "male", null, ""),
            ],
        };

        var groups = SpeechPageRules.BuildVoiceGroups(["kokoro", "google"], lists);

        Assert.Equal(["Kokoro · British English", "Kokoro · French"], groups.Select(g => g.Label));
    }

    [Fact]
    public void Gsx_BeforeSave_PrunesTheLists_AndUpperCasesHubs()
    {
        var gsx = new GsxOptions
        {
            DepartureServices = [new DepartureServiceStep("Refueling", GsxServiceActivation.AfterCalled), new DepartureServiceStep("  ", GsxServiceActivation.Manual)],
            OperatorPreferences = [" Swissport ", "", "dnata"],
            CompanyHubs = ["eg", " LF", " "],
        };

        GsxPageRules.BeforeSave(gsx);

        Assert.Single(gsx.DepartureServices);
        Assert.Equal(["Swissport", "dnata"], gsx.OperatorPreferences);
        Assert.Equal(["EG", "LF"], gsx.CompanyHubs);
    }

    [Theory]
    [InlineData("", false, "never")]
    [InlineData("", true, "always")]
    [InlineData("onlyJetway", true, "onlyJetway")]
    public void Gsx_EffectivePcaMode_FallsBackToTheLegacyBool(string pcaMode, bool autoPca, string expected)
        => Assert.Equal(expected, GsxPageRules.EffectivePcaMode(new GsxOptions { PcaMode = pcaMode, AutoPca = autoPca }));

    [Fact]
    public void Gsx_SceneryFolders_RoundTripThroughTheText()
    {
        var gsx = new GsxOptions();

        GsxPageRules.SetSceneryFolders(gsx, " \"C:\\MSFS\\Community\" ; D:\\Store ;; ");

        Assert.Equal(["C:\\MSFS\\Community", "D:\\Store"], gsx.SceneryPackageFolders);
        Assert.Equal("C:\\MSFS\\Community; D:\\Store", GsxPageRules.SceneryFoldersText(gsx));
    }

    [Fact]
    public void Audio_BeforeSave_PrunesMappings_AndTidiesThePanelLists()
    {
        var audio = new AudioOptions
        {
            AppMappings = [new AudioAppMapping(AudioChannel.Vhf1, " vPilot ") { Device = " Speakers " }, new AudioAppMapping(AudioChannel.Cabin, "  ")],
            DeviceBlacklist = [" Realtek ", ""],
            ActiveAcps = [AcpSide.Captain, AcpSide.Captain],
        };
        var mixer = new MixerOptions
        {
            Mappings = [new MixerMapping { Acp = AcpSide.Observer, Channel = AudioChannel.Vhf1 }],
            PanelStrips = [3, 1, 3],
            PanelBuses = [2, 0],
        };

        AudioPageRules.BeforeSave(audio, mixer);

        Assert.Single(audio.AppMappings);
        Assert.Equal("vPilot", audio.AppMappings[0].Binary);
        Assert.Equal("Speakers", audio.AppMappings[0].Device);
        Assert.Equal(["Realtek"], audio.DeviceBlacklist);
        Assert.Single(audio.ActiveAcps);
        Assert.Equal([1, 3], mixer.PanelStrips);
        Assert.Equal([0, 2], mixer.PanelBuses);
        Assert.Single(mixer.Mappings); // VHF1 exists on the observer panel; only the loudspeaker dial does not
    }

    [Fact]
    public void Audio_NormalizeVoiceMeeterAcpNames_FoldsHandEditedCasing()
    {
        var audio = new AudioOptions { VoiceMeeterMappings = { ["Captain"] = [new VoiceMeeterTargetMapping()], ["FIRSTOFFICER"] = [], ["bogus"] = [] } };

        AudioPageRules.NormalizeVoiceMeeterAcpNames(audio);

        Assert.Equal(["captain", "firstOfficer"], audio.VoiceMeeterMappings.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal("firstOfficer", AudioPageRules.AcpName(AcpSide.FirstOfficer));
    }

    [Theory]
    [InlineData("1, 2, 4", new[] { 0, 1, 3 })]
    [InlineData("4;2;2; x; 0", new[] { 1, 3 })]
    [InlineData("", new int[0])]
    public void Audio_OneBasedLists_ParseAndPrint(string text, int[] expected)
    {
        Assert.Equal(expected, AudioPageRules.ParseOneBasedList(text));
        Assert.Equal(string.Join(", ", expected.Select(i => i + 1)), AudioPageRules.OneBasedText(expected));
    }

    [Fact]
    public void Setup_MigrateLegacyWake_AddsARowOnce_AndSwitchesTheLegacyBlockOff()
    {
        var briefing = new BriefingOptions { LlmWakeOnLan = { Enabled = true, MacAddress = "AA-BB-CC-DD-EE-FF", BroadcastAddress = "192.168.1.255", Port = 7 } };
        var wake = new WakeTargetsOptions();

        SetupPageRules.MigrateLegacyWake(briefing, wake);
        SetupPageRules.MigrateLegacyWake(briefing, wake);

        var row = Assert.Single(wake.Targets);
        Assert.Equal("Language-model PC", row.Name);
        Assert.Equal("192.168.1.255", row.BroadcastAddress);
        Assert.Equal(7, row.Port);
        Assert.False(briefing.LlmWakeOnLan.Enabled);
    }

    [Fact]
    public void Setup_MigrateLegacyWake_RecognisesTheSameMacInAnotherSpelling()
    {
        var briefing = new BriefingOptions { LlmWakeOnLan = { Enabled = true, MacAddress = "aa:bb:cc:dd:ee:ff" } };
        var wake = new WakeTargetsOptions { Targets = [new WakeTarget { MacAddress = "AA-BB-CC-DD-EE-FF" }] };

        SetupPageRules.MigrateLegacyWake(briefing, wake);

        Assert.Single(wake.Targets);
        Assert.False(briefing.LlmWakeOnLan.Enabled);
        Assert.Equal("AABBCCDDEEFF", SetupPageRules.NormalizeMac(" AA:BB-CC DD:EE:FF "));
    }
}
