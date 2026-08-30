using ProsimCompanion.Speech.Recognition;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>Issue #119: whisper writes numbers as digits or words freely; "Flaps 1" must
/// match "flaps one", never "flaps two".</summary>
public sealed class CommandMatcherTests
{
    private static readonly string[] FlapVocabulary =
        ["flaps one", "flaps two", "flaps three", "flaps full"];

    [Theory]
    [InlineData("flaps 1", "flaps one")]
    [InlineData("Flaps 2", "flaps two")]
    [InlineData("flaps 3", "flaps three")]
    [InlineData("starting engine 1", "starting engine one")]
    public void SingleDigits_SnapToTheirWordPhrase(string heard, string expected)
    {
        var vocabulary = heard.StartsWith("starting", StringComparison.Ordinal)
            ? new[] { "starting engine one", "starting engine two" }
            : FlapVocabulary;

        var match = CommandMatcher.Snap(heard, vocabulary, 0.7);

        Assert.NotNull(match);
        Assert.Equal(expected, match.Command);
    }

    [Fact]
    public void WordForm_StillScoresPerfect()
    {
        var match = CommandMatcher.Snap("flaps three", FlapVocabulary, 0.7);

        Assert.NotNull(match);
        Assert.Equal("flaps three", match.Command);
        Assert.True(match.Score >= 0.99);
    }

    [Theory]
    [InlineData("flaps 1", "flaps one")]
    [InlineData("engine 2 start", "engine two start")]
    [InlineData("flaps one", "flaps one")] // unchanged input returns the same instance
    [InlineData("set 121 5 on box 1", "set 121 five on box one")] // "121" untouched; lone digits map
    [InlineData("squawk 3167", "squawk 3167")]
    public void SpeakSingleDigits_MapsOnlyStandaloneSingleDigits(string input, string expected)
        => Assert.Equal(expected, CommandMatcher.SpeakSingleDigits(input));
}
