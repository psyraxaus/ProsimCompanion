using ProsimCompanion.Core.Debrief;
using ProsimCompanion.Speech.Llm;
using Xunit;

namespace ProsimCompanion.Core.Tests.Debrief;

/// <summary>Issue #146: the touchdown rate line in the spoken template and the LLM fact
/// sheet, and the number verifier holding the styled text to it.</summary>
public sealed class DebriefTouchdownRateTests
{
    private static DebriefFacts Facts(double? rate, int? bounces = null) => DebriefFacts.Empty with
    {
        TouchdownGroundSpeedKt = 131,
        TouchdownVerticalSpeedFpm = rate,
        Bounces = bounces,
    };

    [Theory]
    [InlineData(-183.4, "minus 180")]
    [InlineData(-185.0, "minus 190")]   // halves round away from zero, never to "even"
    [InlineData(-64.0, "minus 60")]
    [InlineData(-4.0, "0")]             // rounds to nothing: no "minus zero"
    [InlineData(20.0, "20")]
    [InlineData(-1240.0, "minus 1240")]
    public void TouchdownRate_SpeaksTheNearestTen_WithTheSignAsAWord(double rate, string expected)
        => Assert.Equal(expected, DebriefTemplate.TouchdownRate(rate));

    [Fact]
    public void Template_GainsTheRateLine_RightAfterTheTouchdownSpeed()
    {
        Assert.Equal(
            "Debrief. Touchdown 131 knots. Touchdown at minus 180 feet per minute. Good flight.",
            DebriefTemplate.Build(Facts(-183.4), DebriefVerbosity.Full));
        Assert.Contains("Touchdown at minus 180 feet per minute.",
            DebriefTemplate.Build(Facts(-183.4), DebriefVerbosity.Brief), StringComparison.Ordinal);
    }

    [Fact]
    public void Template_WithoutARecordedRate_IsUnchanged()
        => Assert.Equal(
            "Debrief. Touchdown 131 knots. Good flight.",
            DebriefTemplate.Build(Facts(null), DebriefVerbosity.Full));

    [Fact]
    public void FactBlock_CarriesTheRateAsTheTemplateSpeaksIt_AndBouncesOnlyWhenThereWereAny()
    {
        var bounced = DebriefLlm.FactBlock(Facts(-183.4, bounces: 2));
        var clean = DebriefLlm.FactBlock(Facts(-183.4, bounces: 0));
        var none = DebriefLlm.FactBlock(Facts(null));

        Assert.Contains("- Touchdown rate (feet per minute): minus 180", bounced, StringComparison.Ordinal);
        Assert.Contains("- Bounces on landing: 2", bounced, StringComparison.Ordinal);
        Assert.DoesNotContain("Bounces", clean, StringComparison.Ordinal);
        Assert.DoesNotContain("Touchdown rate", none, StringComparison.Ordinal);
    }

    [Fact]
    public void NumberVerifier_AcceptsTheRate_AndRejectsAnInventedOne()
    {
        var allowed = DebriefLlm.AllowedNumbers(Facts(-183.4));

        Assert.Contains(180, allowed);
        Assert.True(NumberVerifier.Check("A gentle one — touchdown at minus 180 feet per minute, 131 knots.", allowed).Ok);
        Assert.True(NumberVerifier.Check("Touchdown at -180 feet per minute.", allowed).Ok);
        // The raw recorder value is NOT offered: the fact sheet shows the rounded figure only.
        Assert.False(NumberVerifier.Check("Touchdown at minus 183 feet per minute.", allowed).Ok);
        Assert.False(NumberVerifier.Check("Touchdown at minus 120 feet per minute.", allowed).Ok);
    }

    [Fact]
    public void NoRate_AddsNothingToTheAllowedSet()
        => Assert.DoesNotContain(180, DebriefLlm.AllowedNumbers(Facts(null)));
}
