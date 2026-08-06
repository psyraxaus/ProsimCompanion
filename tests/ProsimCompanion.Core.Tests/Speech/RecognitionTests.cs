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
    [InlineData("F12", 0x7B)]
    [InlineData("space", 0x20)]
    [InlineData("RightCtrl", 0xA3)]
    [InlineData("a", 0x41)]
    [InlineData("", 0)]
    public void PushToTalk_ParsesKeyNames(string key, int expected)
        => Assert.Equal(expected, PushToTalkService.ParseKey(key));
}
