using ProsimCompanion.Core.Airports;
using ProsimCompanion.Core.Speech;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>The spoken-text module (campaign #81, CONTEXT.md "spoken text"): one runway
/// renderer, one ICAO speller, one airport-fallback policy.</summary>
public sealed class SpokenTextTests
{
    private sealed class FakeNames : IAirportNames
    {
        public string? SpokenName(string? icao)
            => string.Equals(icao, "EGLL", StringComparison.OrdinalIgnoreCase) ? "Heathrow" : null;
    }

    [Theory]
    [InlineData("16R", "one six right")]
    [InlineData("04L", "zero four left")]
    [InlineData("9", "niner")]
    [InlineData("27C", "two seven center")]
    [InlineData("RW34", "three four")]
    [InlineData("34", "three four")]
    public void Runway_RendersDigitsAndSideWords(string designator, string expected)
        => Assert.Equal(expected, SpokenText.RunwayOrNull(designator));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("ABC")]
    public void Runway_IsNullWhenThereIsNothingToSpeak(string? designator)
        // Absence wording is the caller's (settled #81) — the module never invents it.
        => Assert.Null(SpokenText.RunwayOrNull(designator));

    [Fact]
    public void Airport_UsesTheFriendlyName_WhenTheResolverKnowsIt()
        => Assert.Equal("Heathrow", new SpokenText(new FakeNames()).Airport("EGLL"));

    [Fact]
    public void Airport_NatoSpellsUnknownIcaos_NeverLetterSpells()
    {
        // "E G C C" letter-spelling is what the TTS engines mangle (issue #68) — the settled
        // fallback is the NATO spelling, everywhere.
        Assert.Equal("Echo Golf Charlie Charlie", new SpokenText(new FakeNames()).Airport("EGCC"));
        Assert.Equal("Echo Golf Charlie Charlie", new SpokenText().Airport("egcc"));
    }

    [Fact]
    public void Airport_IsEmptyForBlankInput()
        => Assert.Equal("", new SpokenText().Airport("  "));

    [Fact]
    public void Icao_SpellsLettersAndDigits()
        => Assert.Equal("Yankee Sierra Sierra Yankee", new SpokenText().Icao("YSSY"));

    [Fact]
    public void Frequency_ReadsDigitsWithDecimal()
        => Assert.Equal("one one zero decimal three zero", new SpokenText().Frequency("110.30"));

    [Fact]
    public void Identifier_DelegatesToNatoPhonetics()
        => Assert.Equal(NatoPhonetics.SpeakIdentifier("VOLA3V"), new SpokenText().Identifier("VOLA3V"));
}
