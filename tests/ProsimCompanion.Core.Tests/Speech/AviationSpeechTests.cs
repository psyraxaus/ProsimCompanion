using ProsimCompanion.Speech.Callouts;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

public sealed class AviationSpeechTests
{
    [Theory]
    [InlineData("climb FL350", "climb flight level three five zero")]      // the "Florida" fix
    [InlineData("FL 070", "flight level seven zero")]                      // leading zero stripped
    [InlineData("heading 90", "heading zero niner zero")]                  // padded to 3 digits
    [InlineData("QNH 1013", "Q N H one zero one three")]
    [InlineData("squawk 4521", "squawk four five two one")]
    [InlineData("wind 270", "wind two seven zero")]
    [InlineData("tune 110.30", "tune one one zero decimal three zero")]    // frequency
    [InlineData("runway 16R", "runway one six right")]
    [InlineData("runway 9", "runway niner")]
    [InlineData("ILS approach", "I L S approach")]
    [InlineData("LOC capture", "localizer capture")]
    [InlineData("descend 3000 ft", "descend 3000 feet")]
    [InlineData("250 kts", "250 knots")]
    [InlineData("check ECAM", "check E CAM")]
    [InlineData("cleared FL level", "cleared flight level level")]         // bare FL
    public void Normalize_KnownShorthand(string input, string expected)
        => Assert.Equal(expected, AviationSpeech.Normalize(input));

    [Fact]
    public void Normalize_IsIdempotent()
    {
        const string input = "FL350, QNH 1013, runway 16R via ILS heading 270";
        var once = AviationSpeech.Normalize(input);
        Assert.Equal(once, AviationSpeech.Normalize(once));
    }

    [Fact]
    public void Normalize_CollapsesWhitespace()
        => Assert.Equal("a b", AviationSpeech.Normalize("a   \t b "));

    [Theory]
    [InlineData("", "")]
    [InlineData("   ", "   ")]
    public void Normalize_EmptyPassthrough(string input, string expected)
        => Assert.Equal(expected, AviationSpeech.Normalize(input));

    [Theory]
    [InlineData("1013", "one zero one three")]
    [InlineData("29.92", "two niner decimal niner two")]
    [InlineData("9", "niner")]
    [InlineData("a1b2", "one two")] // non-digits silently dropped
    public void ToDigits_RendersDigitWords(string input, string expected)
        => Assert.Equal(expected, Aviation.ToDigits(input));
}
