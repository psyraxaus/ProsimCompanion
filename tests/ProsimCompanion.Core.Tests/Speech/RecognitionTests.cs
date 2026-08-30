using Microsoft.Extensions.Options;
using Moq;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Speech.Recognition;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

public sealed class RecognitionTests
{
    private readonly SpeechOptions _options = new();

    private UtteranceInterpreter Interpreter()
    {
        var monitor = new Mock<IOptionsMonitor<SpeechOptions>>();
        monitor.SetupGet(m => m.CurrentValue).Returns(() => _options);
        return new UtteranceInterpreter(monitor.Object);
    }

    [Theory]
    [InlineData("QNH 1017 set", 1017)]
    [InlineData("one zero one seven set", 1017)]
    [InlineData("two niner decimal niner two", 29.92)]
    [InlineData("set 121.5 please", 121.5)]
    public void NumberExtractor_FindsEmbeddedNumbers(string utterance, double expected)
    {
        Assert.True(NumberExtractor.TryExtract(utterance, out var value));
        Assert.Equal(expected, value, precision: 3);
    }

    [Fact]
    public void NumberExtractor_NoNumber_ReturnsFalse()
        => Assert.False(NumberExtractor.TryExtract("checked and set", out _));

    [Fact]
    public void CommandMatcher_SnapsNearMiss()
    {
        var match = CommandMatcher.Snap("skip the item", VoiceCommands.All, 0.7);
        Assert.NotNull(match);
        Assert.Equal("skip item", match!.Command);
    }

    [Fact]
    public void CommandMatcher_NothingClearsThreshold_ReturnsNull()
        => Assert.Null(CommandMatcher.Snap("completely unrelated words", ["cancel checklist"], 0.7));

    [Fact]
    public void Interpreter_ExactMatch_ResolvesAtFullScore()
    {
        var result = Interpreter().Interpret("Say Again", VoiceCommands.All,
            new InterpretContext(false, 0.9, 0.1));

        Assert.Equal(InterpretKind.Resolved, result.Kind);
        Assert.Equal("say again", result.Text);
        Assert.Equal(1.0, result.Score);
    }

    [Fact]
    public void Interpreter_HighAmbientNoise_RejectsCommandWindowOnly()
    {
        var noisy = new InterpretContext(false, 0.9, NoSpeechProb: 0.8);
        Assert.Equal(InterpretKind.Reject, Interpreter().Interpret("skip", VoiceCommands.All, noisy).Kind);

        // An awaiting readback is deliberately exempt — the dataref backstop covers it.
        var awaiting = new InterpretContext(true, 0.9, NoSpeechProb: 0.8);
        Assert.Equal(InterpretKind.Resolved, Interpreter().Interpret("skip", VoiceCommands.All, awaiting).Kind);
    }

    [Fact]
    public void Interpreter_GrayBand_AsksForConfirmation()
    {
        // Force a snap into [threshold, confirmBelowScore).
        _options.SnappingThreshold = 0.4;
        _options.ConfirmBelowScore = 0.99;
        var result = Interpreter().Interpret("skip the item", VoiceCommands.All,
            new InterpretContext(false, 0.9, 0.1));

        Assert.Equal(InterpretKind.Confirm, result.Kind);
    }

    [Fact]
    public void Interpreter_AwaitingItem_PassesRawTextThrough()
    {
        var result = Interpreter().Interpret("flaps one and checked", ["checked"],
            new InterpretContext(true, 0.7, 0.1));

        Assert.Equal(InterpretKind.Resolved, result.Kind);
        Assert.Equal("flaps one and checked", result.Text);
    }

    [Theory]
    [InlineData("Thank you.")]
    [InlineData("thanks")]
    [InlineData("Checked.")]
    [InlineData("you")]
    public void Interpreter_KnownHallucination_IsAbsorbedNotRejected(string heard)
    {
        // Issue #120: 13 of 25 rejects on 2026-08-29 were whisper silence artifacts, each an
        // audible FO chirp in cruise.
        var result = Interpreter().Interpret(heard, VoiceCommands.All,
            new InterpretContext(false, 0.9, 0.1));

        Assert.Equal(InterpretKind.Hallucination, result.Kind);
    }

    [Fact]
    public void Interpreter_HallucinationWord_StillAnswersAnAwaitingItem()
    {
        // "checked" is on the hallucination list AND the universal checklist answer — while
        // an item awaits, the answer path must win.
        var result = Interpreter().Interpret("Checked.", ["checked"],
            new InterpretContext(true, 0.9, 0.1));

        Assert.Equal(InterpretKind.Resolved, result.Kind);
    }

    [Fact]
    public void Interpreter_DigitForm_ResolvesExactly()
    {
        // Issue #119: "Flaps 1" must equal "flaps one" — on 2026-08-29 it confirmed
        // "flaps two" instead.
        var result = Interpreter().Interpret("Flaps 1.", ["flaps one", "flaps two"],
            new InterpretContext(false, 0.9, 0.1));

        Assert.Equal(InterpretKind.Resolved, result.Kind);
        Assert.Equal("flaps one", result.Text);
        Assert.Equal(1.0, result.Score);
    }

    [Theory]
    [InlineData("F12", 0x7B)]
    [InlineData("space", 0x20)]
    [InlineData("RightCtrl", 0xA3)]
    [InlineData("a", 0x41)]
    [InlineData("", 0)]
    public void PushToTalk_ParsesKeyNames(string key, int expected)
        => Assert.Equal(expected, PushToTalkService.ParseKey(key));

    [Theory]
    [InlineData(0x41, "A")]        // letter
    [InlineData(0x37, "7")]        // digit
    [InlineData(0x7B, "F12")]      // function key
    [InlineData(0x87, "F24")]      // top of the F range
    [InlineData(0xA3, "rightctrl")]
    [InlineData(0x14, "capslock")]
    [InlineData(0xB3, "179")]      // media key — no name, decimal code
    public void PushToTalk_FormatsCapturedKeys(int vk, string expected)
        => Assert.Equal(expected, PushToTalkService.FormatKey(vk));

    [Theory]
    [InlineData(0x41)]
    [InlineData(0x30)]
    [InlineData(0x70)]
    [InlineData(0x87)]
    [InlineData(0x20)]
    [InlineData(0xA0)]
    [InlineData(0xA5)]
    [InlineData(0x91)]
    [InlineData(0xB3)]
    public void PushToTalk_CapturedKeyNames_RoundTripThroughParseKey(int vk)
        => Assert.Equal(vk, PushToTalkService.ParseKey(PushToTalkService.FormatKey(vk)));

    // Device 0 = pedals, device 3 = the stick; null = not connected.
    private static string? JoystickName(int id) => id switch
    {
        0 => "MFG Crosswind V2",
        3 => "VIRPIL Constellation ALPHA-R",
        _ => null,
    };

    [Fact]
    public void PushToTalk_JoystickNameMatch_WinsOverStaleId()
        // The stick moved from id 1 to id 3 after a re-plug; the stored name still finds it.
        => Assert.Equal(3, PushToTalkService.ResolveJoystickId("VIRPIL Constellation", 1, JoystickName));

    [Fact]
    public void PushToTalk_JoystickNamePrefixMatchesBothWays()
        // The configured name may be LONGER than winmm's 31-char truncated product name.
        => Assert.Equal(0, PushToTalkService.ResolveJoystickId("MFG Crosswind V2 rudder pedals", null, JoystickName));

    [Fact]
    public void PushToTalk_JoystickNoNameConfigured_FallsBackToNumericId()
        => Assert.Equal(1, PushToTalkService.ResolveJoystickId("", 1, JoystickName));

    [Fact]
    public void PushToTalk_JoystickNameNotFound_FallsBackToNumericId_ThenUnbound()
    {
        Assert.Equal(1, PushToTalkService.ResolveJoystickId("Thrustmaster", 1, JoystickName));
        Assert.Equal(-1, PushToTalkService.ResolveJoystickId("Thrustmaster", null, JoystickName));
    }

    [Theory]
    [InlineData("system.analog.A_FC_FO_PITCH", "system.analog.A_FC_CAPT_PITCH")]
    [InlineData("system.analog.A_FC_CAPT_ROLL", "system.analog.A_FC_FO_ROLL")] // involution
    [InlineData("system.switches.S_CDU2_KEY_FPLN", "system.switches.S_CDU1_KEY_FPLN")]
    [InlineData("aircraft.mcdu2.display", "aircraft.mcdu1.display")]
    [InlineData("system.gates.B_FCU_EFIS2_BARO_STD", "system.gates.B_FCU_EFIS1_BARO_STD")]
    [InlineData("aircraft.fms.perf.takeOff.v1", "aircraft.fms.perf.takeOff.v1")] // side-independent
    public void PilotSeat_RightSeat_SwapsSideDependentDatarefs(string authored, string mapped)
        => Assert.Equal(mapped, PilotSeatMap.Map(authored, humanIsRightSeat: true));

    [Fact]
    public void PilotSeat_LeftSeat_IsIdentity()
        => Assert.Equal("system.analog.A_FC_FO_PITCH",
            PilotSeatMap.Map("system.analog.A_FC_FO_PITCH", humanIsRightSeat: false));

    [Fact]
    public void HumanTiming_Disabled_IsIdentity()
    {
        var off = new HumanizeOptions { Enabled = false };
        Assert.Equal(150, ProsimCompanion.Speech.Commands.HumanTiming.Hold(off, 150, new Random(1)));
        Assert.Equal(120, ProsimCompanion.Speech.Commands.HumanTiming.Gap(off, 120, afterPageKey: true, new Random(1)));
    }

    [Fact]
    public void HumanTiming_NeverUndercutsTheConfiguredDelay()
    {
        // The configured delay is a functional minimum (MCDU page-change time).
        var options = new HumanizeOptions();
        var rng = new Random(42);
        for (var i = 0; i < 200; i++)
        {
            Assert.True(ProsimCompanion.Speech.Commands.HumanTiming.Gap(options, 300, i % 2 == 0, rng) >= 300);
            Assert.True(ProsimCompanion.Speech.Commands.HumanTiming.Hold(options, 150, rng) >= 40);
        }
    }

    [Theory]
    [InlineData("system.switches.S_CDU2_KEY_FPLN", true)]   // page key — scan pause likely
    [InlineData("CDU_KEY_PERF", true)]
    [InlineData("system.switches.S_CDU2_KEY_LSK3L", false)] // in-flow
    [InlineData("system.switches.S_CDU1_KEY_CLEAR", false)]
    [InlineData("system.switches.S_FCU_AP1", false)]        // not a CDU key at all
    public void HumanTiming_PageKeyDetection(string dataref, bool isPageKey)
        => Assert.Equal(isPageKey, ProsimCompanion.Speech.Commands.HumanTiming.IsPageKey(dataref));
}
