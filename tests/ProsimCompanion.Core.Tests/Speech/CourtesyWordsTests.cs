using Microsoft.Extensions.Options;
using Moq;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Speech.Recognition;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>Owner report 2026-10-25: "run the departure briefing please" earned a "did you
/// mean …?" because the trailing courtesy cost edit distance. Politeness at the edges is
/// stripped before matching; a bare courtesy is left whole for the hallucination filter and
/// the courtesy reply.</summary>
public sealed class CourtesyWordsTests
{
    [Theory]
    [InlineData("run the departure briefing please", "run the departure briefing")]
    [InlineData("please run the departure briefing", "run the departure briefing")]
    [InlineData("could you please run the departure briefing", "run the departure briefing")]
    [InlineData("can you run the departure briefing for me", "run the departure briefing")]
    [InlineData("please gear up thanks", "gear up")]
    [InlineData("gear up thank you very much", "gear up")]
    [InlineData("kindly set flaps one", "set flaps one")]
    [InlineData("gear up cheers", "gear up")]
    public void Strip_RemovesCourtesyAtTheEdges(string heard, string expected)
        => Assert.Equal(expected, CourtesyWords.Strip(CommandMatcher.Normalize(heard)));

    [Theory]
    [InlineData("gear up")]
    [InlineData("say please to the purser")]
    [InlineData("thanks for the briefing any questions")]
    public void Strip_LeavesCourtesyInsideAPhrase(string heard)
        => Assert.Equal(CommandMatcher.Normalize(heard), CourtesyWords.Strip(CommandMatcher.Normalize(heard)));

    [Theory]
    [InlineData("thank you")]
    [InlineData("thanks")]
    [InlineData("please")]
    [InlineData("thank you very much")]
    public void Strip_NeverEmptiesTheUtterance(string heard)
        => Assert.Equal(heard, CourtesyWords.Strip(heard));

    [Theory]
    [InlineData("thank you", true)]
    [InlineData("thanks", true)]
    [InlineData("cheers", true)]
    [InlineData("thanks very much", true)]
    [InlineData("thank you captain", true)]
    [InlineData("thank you for the briefing", false)]
    [InlineData("gear up", false)]
    [InlineData("", false)]
    public void IsOnlyCourtesy_RecognisesABareThankYou(string normalized, bool expected)
        => Assert.Equal(expected, CourtesyWords.IsOnlyCourtesy(normalized));

    [Theory]
    [InlineData("run the departure briefing please")]
    [InlineData("Please run the departure briefing.")]
    [InlineData("Gear up, please.")]
    public void Interpreter_ResolvesAPoliteCommandExactly(string heard)
    {
        var interpreter = new UtteranceInterpreter(Options(new SpeechOptions()));
        string[] vocabulary = ["run the departure briefing", "gear up", "gear down", "flaps one"];

        var result = interpreter.Interpret(heard, vocabulary, new InterpretContext(false, null, null));

        Assert.Equal(InterpretKind.Resolved, result.Kind);
        Assert.Equal(1.0, result.Score);
        Assert.Contains(result.Text, vocabulary);
    }

    // Owner request 2026-11-07: the person addressed is not part of the request.
    [Theory]
    [InlineData("Ground, please start boarding.", "start boarding")]
    [InlineData("ground crew start boarding", "start boarding")]
    [InlineData("Cabin crew, commence boarding", "commence boarding")]
    [InlineData("let's start the fuel", "start the fuel")]
    [InlineData("can we begin refuelling please", "begin refuelling")]
    [InlineData("ground from cockpit", "ground from cockpit")] // a hail stays a hail
    [InlineData("ground crew", "ground crew")]                 // the bare hail too
    public void Interpreter_StripsTheAddress_ButKeepsAHail(string heard, string expected)
    {
        var interpreter = new UtteranceInterpreter(Options(new SpeechOptions()));
        string[] vocabulary = ["start boarding", "commence boarding", "start the fuel", "begin refuelling", "ground from cockpit", "ground crew", "gear up"];

        var result = interpreter.Interpret(heard, vocabulary, new InterpretContext(false, null, null));

        Assert.Equal(InterpretKind.Resolved, result.Kind);
        Assert.Equal(expected, result.Text);
        Assert.Equal(1.0, result.Score);
    }

    [Fact]
    public void AddressWords_StripOnlyTheFront_AndNeverToNothing()
    {
        Assert.Equal("start boarding", AddressWords.Strip("ground please start boarding"));
        Assert.Equal("start boarding", AddressWords.Strip("cabin crew lets start boarding"));
        Assert.Equal("ground", AddressWords.Strip("ground"));
        Assert.Equal("connect ground power", AddressWords.Strip("connect ground power")); // inside a phrase: untouched
    }

    [Fact]
    public void Interpreter_StillAbsorbsABareThankYouAsHallucination()
    {
        var interpreter = new UtteranceInterpreter(Options(new SpeechOptions()));

        var result = interpreter.Interpret("Thank you.", ["gear up"], new InterpretContext(false, null, null));

        Assert.Equal(InterpretKind.Hallucination, result.Kind);
    }

    private static IOptionsMonitor<SpeechOptions> Options(SpeechOptions value)
    {
        var monitor = new Mock<IOptionsMonitor<SpeechOptions>>();
        monitor.SetupGet(m => m.CurrentValue).Returns(value);
        return monitor.Object;
    }
}
