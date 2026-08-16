using ProsimCompanion.Core.Speech;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>The shared letter↔NATO table + identifier rendering (issue #68).</summary>
public sealed class NatoPhoneticsTests
{
    [Theory]
    [InlineData("VOLA3V", "VOLA three Victor")]     // the issue's SID example
    [InlineData("KODAP1A", "KODAP one Alpha")]
    [InlineData("FISHA1", "FISHA one")]
    [InlineData("ILS09L", "ILS zero niner Lima")]   // 3+ letter base kept, rest phonetic
    [InlineData("ILS 16R", "ILS one six Romeo")]    // whitespace splits tokens
    [InlineData("V", "Victor")]                      // single letter
    [InlineData("BH1", "Bravo Hotel one")]           // short base → letters phonetic
    [InlineData("123", "one two three")]             // all digits
    [InlineData("H65", "Hotel six five")]            // airway
    public void SpeakIdentifier_RendersForTts(string identifier, string expected)
        => Assert.Equal(expected, NatoPhonetics.SpeakIdentifier(identifier));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SpeakIdentifier_BlankInput_EmptyOutput(string? identifier)
        => Assert.Equal("", NatoPhonetics.SpeakIdentifier(identifier));

    [Theory]
    [InlineData("M", "Mike")]
    [InlineData("v", "Victor")]  // case-insensitive
    [InlineData("J", "Juliet")]  // TTS-friendly spelling, not ICAO "Juliett"
    [InlineData("X", "X-ray")]
    public void Letter_SingleLetterBecomesWord(string letter, string expected)
        => Assert.Equal(expected, NatoPhonetics.Letter(letter));

    [Fact]
    public void Letter_NullAndMultiCharPassThrough()
    {
        Assert.Null(NatoPhonetics.Letter(null));        // omitted fact lines stay omitted
        Assert.Null(NatoPhonetics.Letter("  "));
        Assert.Equal("MM", NatoPhonetics.Letter("MM")); // never mangle longer values
        Assert.Equal("7", NatoPhonetics.Letter("7"));   // non-letter single char passes through
    }

    [Theory]
    [InlineData("mike", 'M')]
    [InlineData("Juliet", 'J')]
    [InlineData("juliett", 'J')] // ICAO spelling accepted on the parse side
    [InlineData("alfa", 'A')]
    [InlineData("xray", 'X')]
    [InlineData("x-ray", 'X')]
    public void TryParseWord_AcceptsSpellingVariants(string word, char expected)
    {
        Assert.True(NatoPhonetics.TryParseWord(word, out var letter));
        Assert.Equal(expected, letter);
    }

    [Fact]
    public void TryParseWord_RejectsNonNatoWords()
        => Assert.False(NatoPhonetics.TryParseWord("banana", out _));
}
