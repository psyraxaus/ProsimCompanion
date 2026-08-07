using ProsimCompanion.Core.Weather;
using Xunit;

namespace ProsimCompanion.Core.Tests.Weather;

public sealed class AtisLetterTests
{
    [Theory]
    [InlineData("Sydney Airport information Juliett, runway 16R in use.", "J")]
    [InlineData("information juliet", "J")] // common misspelling also maps to J
    [InlineData("ATIS Delta 070800Z", "D")]
    [InlineData("info x", "X")]
    public void InformationKeyword_ExtractsFollowingToken(string atis, string expected)
        => Assert.Equal(expected, AtisLetter.Extract(atis));

    [Theory]
    [InlineData("Bravo wind two seven zero at one two", "B")] // broadcast opening with the word
    [InlineData("Kilo", "K")]
    public void FirstToken_UsedWhenNoKeyword(string atis, string expected)
        => Assert.Equal(expected, AtisLetter.Extract(atis));

    [Fact]
    public void FirstToken_WithTrailingPunctuation_IsUnidentifiable()
        // Predecessor semantics kept exactly: tokens split on spaces only, so "Bravo." (with
        // the period attached) does not resolve — better null than a guessed letter.
        => Assert.Null(AtisLetter.Extract("Bravo. Wind two seven zero."));

    [Theory]
    [InlineData("c", "C")]
    [InlineData("Q", "Q")]
    public void BareLetter_PassesThroughUppercased(string atis, string expected)
        => Assert.Equal(expected, AtisLetter.Extract(atis));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("12345")]
    [InlineData("Runway 16R in use, expect ILS approach.")] // no identifiable letter
    public void Unidentifiable_ReturnsNull(string? atis)
        => Assert.Null(AtisLetter.Extract(atis));
}
