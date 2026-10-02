using ProsimCompanion.Speech.Llm;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>Issue #147, owner decision D4: numbers a model wrote as words are turned back
/// into digits so the number verifier can see them.</summary>
public sealed class SpokenNumberTextTests
{
    [Theory]
    // digit by digit — what the briefing prompt asks the model to write
    [InlineData("heading one six three", "heading 163")]
    [InlineData("wind zero nine zero at one four knots", "wind 090 at 14 knots")]
    [InlineData("frequency one one eight decimal one zero", "frequency 118.10")]
    [InlineData("flight level one one zero", "flight level 110")]
    [InlineData("runway one six right", "runway 16 right")]
    [InlineData("squawk seven five zero zero.", "squawk 7500.")]
    [InlineData("QNH one zero one three, minimums two one zero feet", "QNH 1013, minimums 210 feet")]
    [InlineData("niner niner eight", "998")]
    // magnitudes — altitudes, speeds and counts "read normally"
    [InlineData("climb to five thousand feet", "climb to 5000 feet")]
    [InlineData("one hundred and fifty two knots", "152 knots")]
    [InlineData("fifteen hundred feet", "1500 feet")]
    [InlineData("twelve thousand nine hundred ninety-nine feet", "12999 feet")]
    [InlineData("glideslope three point five degrees", "glideslope 3.5 degrees")]
    [InlineData("twenty five checklists", "25 checklists")]
    [InlineData("a hundred percent", "a 100 percent")]
    // left alone
    [InlineData("one of the two engines", "one of the two engines")]
    [InlineData("V one, rotate, V two", "V one, rotate, V two")]
    [InlineData("descend to FL350 for traffic", "descend to FL350 for traffic")]
    [InlineData("I ate for two, oh well", "I ate for two, oh well")]
    [InlineData("QNH 1013 and runway 16R", "QNH 1013 and runway 16R")]
    [InlineData("", "")]
    public void ToDigits_ForVerification(string text, string expected)
        => Assert.Equal(expected, SpokenNumberText.ToDigits(text));

    [Fact]
    public void PunctuationEndsANumber_SoTwoNumbersAreNeverGluedTogether()
    {
        Assert.Equal("V1 141, 144, 147", SpokenNumberText.ToDigits("V1 one four one, one four four, one four seven"));
        Assert.Equal("163. 250", SpokenNumberText.ToDigits("one six three. two five zero"));
    }

    [Fact]
    public void AnAndThatIsNotInsideAMagnitude_IsJustAWord()
        => Assert.Equal("250 and 14", SpokenNumberText.ToDigits("two five zero and one four"));

    [Fact]
    public void ADecimalWordNotFollowedByADigit_IsJustAWord()
        => Assert.Equal("163 point taken", SpokenNumberText.ToDigits("one six three point taken"));

    [Fact]
    public void CaseDoesNotMatter()
        => Assert.Equal("Heading 163", SpokenNumberText.ToDigits("Heading One Six Three"));

    [Theory]
    [InlineData("one defect and four checklists", "1 defect and 4 checklists")]
    [InlineData("V one, rotate", "V 1, rotate")]
    [InlineData("heading one six three", "heading 163")]
    public void IncludeSingles_AlsoRewritesALoneDigitWord_ForMatchingOnly(string text, string expected)
        => Assert.Equal(expected, SpokenNumberText.ToDigits(text, includeSingles: true));

    [Fact]
    public void TheVerifier_NowCatchesAWrongSpelledNumber()
    {
        double[] allowed = [163, 1013, 118.10];

        // Before this pass both lines "verified": there was not one digit in them to check.
        Assert.True(NumberVerifier.Check("Initial track one six three degrees.", allowed).Ok);
        Assert.True(NumberVerifier.Check("Initial track one six eight degrees.", allowed).Ok);

        Assert.True(NumberVerifier.Check(SpokenNumberText.ToDigits("Initial track one six three degrees."), allowed).Ok);
        var wrong = NumberVerifier.Check(SpokenNumberText.ToDigits("Initial track one six eight degrees."), allowed);
        Assert.False(wrong.Ok);
        Assert.Equal("168", Assert.Single(wrong.Offending));
        Assert.True(NumberVerifier.Check(SpokenNumberText.ToDigits("Tower on one one eight decimal one zero."), allowed).Ok);
        Assert.False(NumberVerifier.Check(SpokenNumberText.ToDigits("Tower on one one eight decimal seven."), allowed).Ok);
    }
}
