using ProsimCompanion.Core.Speech;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>The spoken lines of the owner's flight 2026-11-14 (EGLL-LOWW) that reached the
/// voice wrong: a runway that swallowed the next word's space, and procedure identifiers and a
/// SayIntentions whole-hundred flight level that went out as written.</summary>
public sealed class PhraseologyFlight20261114Tests
{
    [Theory]
    [InlineData("Cleared for the ILS runway 34 approach", "runway three four approach")]
    [InlineData("runway 34 vacated, request taxi", "runway three four vacated, request taxi")]
    [InlineData("Runway 34. Cleared to land", "runway three four. Cleared to land")]
    [InlineData("holding point runway 27R via Alpha", "runway two seven right via")]
    [InlineData("runway 27 L in use", "runway two seven left in use")]
    [InlineData("runway 09 cleared for takeoff", "runway zero niner cleared for takeoff")]
    public void ARunway_KeepsTheSpaceBeforeTheNextWord(string text, string expected)
        => Assert.Contains(expected, Phraseology.Normalize(text), StringComparison.OrdinalIgnoreCase);

    [Theory]
    [InlineData("Cleared for the BUDE2W arrival, Speedbird seven two two.", "Cleared for the Bude two Whiskey arrival, Speedbird seven two two.")]
    [InlineData("Expect the BUDE2W arrival.", "Expect the Bude two Whiskey arrival.")]
    [InlineData("climbing through two thousand five hundred on KEMA5C.", "climbing through two thousand five hundred on Kema five Charlie.")]
    [InlineData("via NER5N transition", "via November Echo Romeo five November transition")]
    [InlineData("approach, via NER5N, Speedbird", "approach, via November Echo Romeo five November, Speedbird")]
    [InlineData("the FISHA1 departure", "the Fisha one departure")]
    public void AProcedureIdentifierInAnAtcLine_IsSaidTheWayACrewSaysIt(string text, string expected)
        => Assert.Equal(expected, Phraseology.Normalize(text));

    [Theory]
    [InlineData("the CAT3 approach")]
    [InlineData("we had ENG1 on the page")]
    [InlineData("hold on TOGA1 please")]
    public void ATokenThatIsNotAProcedure_IsNotSpelledAsOne(string text)
    {
        var spoken = Phraseology.Normalize(text);
        Assert.DoesNotContain("Charlie", spoken, StringComparison.Ordinal);
        Assert.DoesNotContain("Echo", spoken, StringComparison.Ordinal);
        Assert.DoesNotContain("Toga one", spoken, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("When ready descend Flight level-3-hundred.", "When ready descend flight level three hundred.")]
    [InlineData("Climb Flight-level-1-hundred.", "Climb flight level one hundred.")]
    public void AWholeHundredLevelAsSayIntentionsWritesIt_IsReadBackAsSaid(string text, string expected)
        => Assert.Equal(expected, Phraseology.Normalize(text));
}
