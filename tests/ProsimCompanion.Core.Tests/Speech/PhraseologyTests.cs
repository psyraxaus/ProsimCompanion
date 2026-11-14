using ProsimCompanion.Core.Speech;
using ProsimCompanion.Speech.Callouts;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

public sealed class PhraseologyTests
{
    [Theory]
    [InlineData("climb FL350", "climb flight level three five zero")]      // the "Florida" fix
    [InlineData("FL 070", "flight level seven zero")]                      // leading zero stripped
    [InlineData("heading 90", "heading zero niner zero")]                  // padded to 3 digits
    [InlineData("QNH 1013", "Q N H one zero one three")]
    [InlineData("squawk 4521", "squawk four five two one")]
    [InlineData("wind 270", "wind two seven zero")]
    [InlineData("winds 270 at 12 knots", "windz two seven zero at one two knots")] // plural rule + respelling (#69)
    [InlineData("Winds 090", "windz zero niner zero")]                        // case-insensitive
    [InlineData("light winds today", "light windz today")]                    // standalone plural respelled
    [InlineData("the wind is calm", "the wind is calm")]                      // singular untouched
    [InlineData("winds aloft gusting 25 and variable", "windz aloft gusting two five and variable")]
    [InlineData("tune 110.30", "tune one one zero decimal three zero")]    // frequency
    [InlineData("runway 16R", "runway one six right")]
    [InlineData("runway 9", "runway niner")]
    [InlineData("ILS approach", "I L S approach")]
    [InlineData("LOC capture", "localizer capture")]
    [InlineData("descend 3000 ft", "descend three thousand feet")]
    [InlineData("250 kts", "two five zero knots")]
    [InlineData("check ECAM", "check ee-cam")]
    [InlineData("cleared FL level", "cleared flight level level")]         // bare FL
    // V-speeds (owner report 2026-10-31): "V1" came out of kokoro as "vone".
    [InlineData("V1, V R, V2, Flex Temp", "Vee one, Vee R, Vee two, Flex Temp")]
    [InlineData("V 1 142, rotate 145, V 2 150, flex 55, set", "Vee one one four two, rotate one four five, Vee two one five zero, flex five five, set")]
    [InlineData("VAPP 134, VLS 129, VREF 130", "Vee app one three four, V L S one two niner, Vee ref one three zero")]
    [InlineData("v1 cut", "Vee one cut")]
    public void Normalize_KnownShorthand(string input, string expected)
        => Assert.Equal(expected, Phraseology.Normalize(input));

    // Letters in context (issue #200, owner report 2026-11-01: the departure briefing listed
    // the closed taxiway NOTAMs as "taxiway G and G5 closed").
    [Theory]
    [InlineData("taxiway G and G5 closed", "taxiway Golf and Golf five closed")]
    [InlineData("taxiways A, B and C closed", "taxiways Alpha, Bravo and Charlie closed")]
    [InlineData("taxiway NA closed", "taxiway November Alpha closed")]
    [InlineData("TWY G5 CLSD", "taxiway Golf five CLSD")]                                  // NOTAM shorthand
    [InlineData("RWY 04R closed by NOTAM", "runway zero four right closed by no-tam")]
    [InlineData("holding point N1", "holding point November one")]                          // a real name, not the engine
    [InlineData("taxi via Z, T, A to holding point runway 24", "taxi via Zulu, Tango, Alpha to holding point runway two four")]
    [InlineData("taxi via B2 and C, hold short of A3", "taxi via Bravo two and Charlie, hold short of Alpha three")]
    [InlineData("Gate 506 via A", "Gate five zero six via Alpha")]
    [InlineData("stand 546R", "stand five four six Romeo")]
    [InlineData("gate B12", "gate Bravo twelve")]
    [InlineData("stand 23 A", "stand twenty three Alpha")]
    [InlineData("information M is current", "information Mike is current")]
    [InlineData("ILS Y runway 27", "I L S Yankee runway two seven")]
    [InlineData("exit at A4", "exit at Alpha four")]
    [InlineData("closed taxiway B2 ahead", "closed taxiway Bravo two ahead")]
    // Left alone: an article, a type, the V-speeds and engine parameters, prose after "on"/"at".
    [InlineData("a runway closed by NOTAM", "a runway closed by no-tam")]
    [InlineData("rotate at V R, climb at V2", "rotate at Vee R, climb at Vee two")]
    [InlineData("N1 at 85 percent, N2 stable", "N1 at eighty five percent, N2 stable")]
    [InlineData("an A320 on ND and IR", "an A320 on N D and IR")]
    [InlineData("level at FL", "level at flight level")]
    [InlineData("via LAM transition", "via LAM transition")]
    [InlineData("set at Q N H, fly the I L S", "set at Q N H, fly the I L S")]                 // letter-spelled abbreviations
    [InlineData("Terminal 5A Gate 506", "Terminal five Alpha Gate five zero six")]
    public void Normalize_LettersInContext(string input, string expected)
        => Assert.Equal(expected, Phraseology.Normalize(input));

    // The net (owner report 2026-11-14: "8700 kilograms" was read "eighty seven, zero"): no
    // figure reaches the voice as digits. Quantities are plain words; speeds, directions,
    // clock times and three-digit stands are digit by digit; names keep their digits.
    [Theory]
    [InlineData("Fuel quantity, 8700 kilograms, loaded", "Fuel quantity, eight thousand seven hundred kilograms, loaded")]
    [InlineData("Zero fuel weight 62.3 tonnes.", "Zero fuel weight sixty two point three tonnes.")]
    [InlineData("Passengers, 148.", "Passengers, one hundred forty eight.")]
    [InlineData("Transition altitude 10000 feet.", "Transition altitude ten thousand feet.")]
    [InlineData("climbing through 2,500 feet", "climbing through two thousand five hundred feet")]
    [InlineData("Destination visibility now 800 metres, below 1500 metres.", "Destination visibility now eight hundred metres, below one thousand five hundred metres.")]
    [InlineData("Touchdown at minus 180 feet per minute.", "Touchdown at minus one hundred eighty feet per minute.")]
    [InlineData("OAT -2", "O A T minus two")]
    [InlineData("We're 22 minutes late off blocks", "We're twenty two minutes late off blocks")]
    [InlineData("Initial track 163 degrees.", "Initial track one six three degrees.")]
    [InlineData("Wind at destination 270 at 8 knots", "Wind at destination two seven zero at eight knots")]
    [InlineData("Wind at destination 090 at 8 knots", "Wind at destination zero niner zero at eight knots")]
    [InlineData("V1 140, rotate 145, V2 150.", "Vee one one four zero, rotate one four five, Vee two one five zero.")]
    [InlineData("Negative — speed 230", "Negative — speed two three zero")]
    [InlineData("scheduled off-blocks 09:00 zulu", "scheduled off-blocks zero niner zero zero zulu")]
    [InlineData("airborne at 14:05Z", "airborne at one four zero five zulu")]
    [InlineData("altimeter 29.92", "altimeter two niner niner two")]
    [InlineData("seat 24C", "seat twenty four Charlie")]
    [InlineData("door 1L open", "door one left open")]
    [InlineData("W36 is 36 Alpha or 36 Bravo", "Whiskey three six is thirty six Alpha or thirty six Bravo")]
    [InlineData("established ILS 27R", "established I L S two seven right")]
    [InlineData("generator 1 fault", "generator one fault")]
    [InlineData("heading 060, 5000 feet", "heading zero six zero, five thousand feet")]
    [InlineData("an A320 at 1.5 percent", "an A320 at one point five percent")]
    [InlineData("Negative — speed 230, flaps two limit is 215.", "Negative — speed two three zero, flaps two limit is two one five.")]
    [InlineData("V one: FMS 140, performance 142", "V one: F M S one four zero, performance one four two")]
    [InlineData("Crosswind component 36 knots, gusting 41, headwind 0 — exceeds the wet-runway crosswind limit of 33 knots.", "Crosswind component three six knots, gusting four one, headwind zero — exceeds the wet-runway crosswind limit of three three knots.")]
    [InlineData("Delay code 1 5, Boarding, noted.", "Delay code one five, Boarding, noted.")]
    [InlineData("about 30 miles north of Turin", "about thirty miles north of Turin")]
    [InlineData("12.5 block hours", "twelve point five block hours")]
    [InlineData("ISA plus 15 — expect reduced climb performance.", "ISA plus fifteen — expect reduced climb performance.")]
    [InlineData("Block fuel raised to 9.1 tonnes — 700 kilos extra.", "Block fuel raised to nine point one tonnes — seven hundred kilos extra.")]
    [InlineData("Total block 1 hour 20 minutes", "Total block one hour twenty minutes")]
    [InlineData("Glideslope 3.0 degrees.", "Glideslope three point zero degrees.")]
    [InlineData("Transition level 110.", "Transition level one one zero.")]
    public void Normalize_LeavesNoQuantityInDigits(string input, string expected)
    {
        var once = Phraseology.Normalize(input);
        Assert.Equal(expected, once);
        Assert.Equal(once, Phraseology.Normalize(once));
    }

    [Theory]
    [InlineData("FL350, QNH 1013, runway 16R via ILS heading 270")]
    [InlineData("taxiway G and G5 closed, hold short of A3, information M, stand 546R, V1 142")]
    public void Normalize_IsIdempotent(string input)
    {
        var once = Phraseology.Normalize(input);
        Assert.Equal(once, Phraseology.Normalize(once));
    }

    [Fact]
    public void Letters_HyphenJoin_IsTheRadiosStyle()
    {
        Assert.Equal("Taxi via Bravo-two and Charlie, hold short of Alpha-three", Phraseology.Letters("Taxi via B2 and C, hold short of A3", "-"));
        Assert.Equal("five-four-six-Romeo", Phraseology.Spell("546R", "-"));
        Assert.Equal("Bravo one six", Phraseology.Spell("B16", " "));
    }

    [Fact]
    public void Normalize_CollapsesWhitespace()
        => Assert.Equal("a b", Phraseology.Normalize("a   \t b "));

    [Theory]
    [InlineData("", "")]
    [InlineData("   ", "   ")]
    public void Normalize_EmptyPassthrough(string input, string expected)
        => Assert.Equal(expected, Phraseology.Normalize(input));

    [Theory]
    [InlineData("1013", "one zero one three")]
    [InlineData("29.92", "two niner decimal niner two")]
    [InlineData("9", "niner")]
    [InlineData("a1b2", "one two")] // non-digits silently dropped
    public void ToDigits_RendersDigitWords(string input, string expected)
    {
        Assert.Equal(expected, Phraseology.Digits(input));
        Assert.Equal(expected, Aviation.ToDigits(input));
    }
}
