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
    [InlineData("winds 270 at 12 knots", "windz two seven zero at 12 knots")] // plural rule + respelling (#69)
    [InlineData("Winds 090", "windz zero niner zero")]                        // case-insensitive
    [InlineData("light winds today", "light windz today")]                    // standalone plural respelled
    [InlineData("the wind is calm", "the wind is calm")]                      // singular untouched
    [InlineData("winds aloft gusting 25 and variable", "windz aloft gusting 25 and variable")]
    [InlineData("tune 110.30", "tune one one zero decimal three zero")]    // frequency
    [InlineData("runway 16R", "runway one six right")]
    [InlineData("runway 9", "runway niner")]
    [InlineData("ILS approach", "I L S approach")]
    [InlineData("LOC capture", "localizer capture")]
    [InlineData("descend 3000 ft", "descend 3000 feet")]
    [InlineData("250 kts", "250 knots")]
    [InlineData("check ECAM", "check E CAM")]
    [InlineData("cleared FL level", "cleared flight level level")]         // bare FL
    // V-speeds (owner report 2026-10-31): "V1" came out of kokoro as "vone".
    [InlineData("V1, V R, V2, Flex Temp", "Vee one, Vee R, Vee two, Flex Temp")]
    [InlineData("V 1 142, rotate 145, V 2 150, flex 55, set", "Vee one 142, rotate 145, Vee two 150, flex 55, set")]
    [InlineData("VAPP 134, VLS 129, VREF 130", "Vee app 134, V L S 129, Vee ref 130")]
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
    [InlineData("RWY 04R closed by NOTAM", "runway zero four right closed by NOTAM")]
    [InlineData("holding point N1", "holding point November one")]                          // a real name, not the engine
    [InlineData("taxi via Z, T, A to holding point runway 24", "taxi via Zulu, Tango, Alpha to holding point runway two four")]
    [InlineData("taxi via B2 and C, hold short of A3", "taxi via Bravo two and Charlie, hold short of Alpha three")]
    [InlineData("Gate 506 via A", "Gate 506 via Alpha")]
    [InlineData("stand 546R", "stand 546 Romeo")]
    [InlineData("gate B12", "gate Bravo 12")]
    [InlineData("stand 23 A", "stand 23 Alpha")]
    [InlineData("information M is current", "information Mike is current")]
    [InlineData("ILS Y runway 27", "I L S Yankee runway two seven")]
    [InlineData("exit at A4", "exit at Alpha four")]
    [InlineData("closed taxiway B2 ahead", "closed taxiway Bravo two ahead")]
    // Left alone: an article, a type, the V-speeds and engine parameters, prose after "on"/"at".
    [InlineData("a runway closed by NOTAM", "a runway closed by NOTAM")]
    [InlineData("rotate at V R, climb at V2", "rotate at Vee R, climb at Vee two")]
    [InlineData("N1 at 85 percent, N2 stable", "N1 at 85 percent, N2 stable")]
    [InlineData("an A320 on ND and IR", "an A320 on ND and IR")]
    [InlineData("level at FL", "level at flight level")]
    [InlineData("via LAM transition", "via LAM transition")]
    [InlineData("set at Q N H, fly the I L S", "set at Q N H, fly the I L S")]                 // letter-spelled abbreviations
    [InlineData("Terminal 5A Gate 506", "Terminal 5A Gate 506")]
    public void Normalize_LettersInContext(string input, string expected)
        => Assert.Equal(expected, Phraseology.Normalize(input));

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
