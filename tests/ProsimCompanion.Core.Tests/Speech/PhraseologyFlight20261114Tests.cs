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

    // Issue #200: the briefing facts were "taxiway LI closed" and "taxiway A, C and TW closed";
    // the model wrote the two-letter taxiways spaced and, in the second briefing, with an
    // Oxford comma. The first two lines are the two speech.segment texts of the flight.
    [Theory]
    [InlineData(
        "Threats: taxiway L I closed; taxiways A, C, and T W closed; icing conditions, engine anti-ice required.",
        "Threats: taxiway Lima India closed; taxiways Alpha, Charlie, and Tango Whiskey closed; icing conditions, engine anti-ice required.")]
    [InlineData(
        "Tech log: number three brake wear indicator at limit, M E L category C. Threats: taxiway L I closed; taxiways A, C and T W closed; icing conditions, engine anti-ice on.",
        "Tech log: number three brake wear indicator at limit, M E L category C. Threats: taxiway Lima India closed; taxiways Alpha, Charlie and Tango Whiskey closed; icing conditions, engine anti-ice on.")]
    [InlineData("Threats today: taxiway LI closed, a taxiway closed, taxiway A, C and TW closed.", "Threats today: taxiway Lima India closed, a taxiway closed, taxiway Alpha, Charlie and Tango Whiskey closed.")]
    [InlineData("taxiway T W.", "taxiway Tango Whiskey.")]
    [InlineData("taxiways B, L I and G5 closed", "taxiways Bravo, Lima India and Golf five closed")]
    [InlineData("taxi via A, B, and C to holding point N1", "taxi via Alpha, Bravo, and Charlie to holding point November one")]
    public void ATwoLetterTaxiwayTheModelSpaced_AndAnOxfordCommaList_AreSpelled(string text, string expected)
    {
        var once = Phraseology.Normalize(text);
        Assert.Equal(expected, once);
        Assert.Equal(once, Phraseology.Normalize(once));
    }

    [Theory]
    [InlineData("taxiway A I think is closed", "taxiway Alpha I think is closed")]           // the pronoun
    [InlineData("holding point A I L S critical area", "holding point Alpha I L S critical area")] // a letter-spelled abbreviation
    [InlineData("via T W", "via T W")]                                                        // the spaced pair is the taxiway context's only
    [InlineData("on E CAM and at Q N H", "on ee-cam and at Q N H")]
    public void ASpacedLetterThatIsNotATaxiway_IsLeftAlone(string text, string expected)
        => Assert.Equal(expected, Phraseology.Normalize(text));

    // Issue #200: the check-in said "stand Pier North Gate Foxtrot one seven" and, 27 seconds
    // later, the read-back of ATC's "Gate F17" said "Gate Foxtrot seventeen". A designator
    // with a letter is said digit by digit in every path.
    [Theory]
    [InlineData("Taxi to Pier North Gate F17 via D, L, Taxilane 37", "Taxi to Pier North Gate Foxtrot one seven via Delta, Lima, Taxilane thirty seven")]
    [InlineData("Taxi to Pier North Gate Foxtrot 17 via Delta, Lima", "Taxi to Pier North Gate Foxtrot one seven via Delta, Lima")]
    [InlineData("gate F 17", "gate Foxtrot one seven")]
    [InlineData("stand 23A", "stand two three Alpha")]
    [InlineData("stand 23 Alpha is ours", "stand two three Alpha is ours")]
    [InlineData("bay B5", "bay Bravo five")]
    [InlineData("stand 546R", "stand five four six Romeo")]
    public void AStandWithALetter_IsDigitByDigit(string text, string expected)
    {
        var once = Phraseology.Normalize(text);
        Assert.Equal(expected, once);
        Assert.Equal(once, Phraseology.Normalize(once));
    }

    // The owner's 2026-10-29 decision stands for cockpit talk: a bare one- or two-digit gate
    // is a number, a three-digit one digits.
    [Theory]
    [InlineData("We park at gate 23 today", "We park at gate twenty three today")]
    [InlineData("stand 7", "stand seven")]
    [InlineData("gate 506", "gate five zero six")]
    public void ABareGateNumberInCockpitTalk_KeepsItsForm(string text, string expected)
        => Assert.Equal(expected, Phraseology.Normalize(text));

    // …and on the radio (RadioSpeech.Spoken — every radio line's FO-voice text) it is digits.
    [Theory]
    [InlineData("Taxi to Gate 17 via Delta, Speedbird-seven-two-two.", "Taxi to Gate one seven via Delta, Speedbird seven two two.")]
    [InlineData("request taxi to stand 23.", "request taxi to stand two three.")]
    [InlineData("request taxi to stand Pier North Gate Foxtrot-one-seven.", "request taxi to stand Pier North Gate Foxtrot one seven.")]
    public void ABareGateNumberOnTheRadio_IsDigitByDigit(string transmit, string expected)
    {
        var spoken = ProsimCompanion.Speech.SayIntentions.RadioSpeech.Spoken(transmit);
        Assert.Equal(expected, spoken);
        Assert.Equal(expected, Phraseology.Normalize(spoken));
    }

    [Fact]
    public void TheReadBackOfTheFlight_SaysTheGateAsTheCheckInDid()
    {
        var readback = ProsimCompanion.Speech.SayIntentions.ReadbackComposer.Compose(
            "Speedbird 722, taxi to Pier North Gate F17 via D, L, Taxilane 37.", "Speedbird-seven-two-two", "Vienna Ground");

        Assert.NotNull(readback);
        Assert.Contains("Pier North Gate Foxtrot-one-seven via Delta, Lima", readback.Transmit, StringComparison.Ordinal);
        Assert.Contains("Pier North Gate Foxtrot one seven via Delta, Lima", Phraseology.Normalize(readback.Spoken), StringComparison.Ordinal);
    }
}
