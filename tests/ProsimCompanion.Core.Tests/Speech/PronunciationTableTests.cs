using ProsimCompanion.Core.Speech;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>Abbreviations said as a word or as letters (owner request 2026-11-14: "ECAM, not
/// E, C, A, M" — likewise SID and STAR).</summary>
public sealed class PronunciationTableTests
{
    [Theory]
    [InlineData("check ECAM", "check ee-cam")]
    [InlineData("ECAM actions at four hundred feet", "ee-cam actions at four hundred feet")]
    [InlineData("the SID is KEMA five Charlie", "the sid is KEMA five Charlie")]
    [InlineData("expect the STAR", "expect the star")]
    [InlineData("two SIDs and three STARs", "two sids and three stars")]
    [InlineData("Destination ATIS now information Bravo.", "Destination ay-tis now information Bravo.")]
    [InlineData("ADIRS", "ay-dirs")]
    [InlineData("TOGA, TCAS, METAR, NOTAM", "toe-ga, tee-cas, mee-tar, no-tam")]
    [InlineData("CAT 3 approach", "cat three approach")]
    // Letters.
    [InlineData("APU bleed", "A P U bleed")]
    [InlineData("ask ATC", "ask A T C")]
    [InlineData("Captain, your PA.", "Captain, your P A.")]
    [InlineData("the FMS and the MCDU", "the F M S and the M C D U")]
    [InlineData("ILS approach", "I L S approach")]
    // Hand-spelled and model-written forms of a word say the word too.
    [InlineData("E CAM Status", "ee-cam Status")]
    [InlineData("A D I R S", "ay-dirs")]
    [InlineData("the A T I S", "the ay-tis")]
    // Only the capital form of a whole word.
    [InlineData("a star in the sky, the cat sat", "a star in the sky, the cat sat")]
    [InlineData("Sidney and STARLING", "Sidney and STARLING")]
    [InlineData("an A320 on ND and IR", "an A320 on N D and IR")]
    public void Normalize_SaysAbbreviationsTheCrewWay(string input, string expected)
    {
        var once = Phraseology.Normalize(input);
        Assert.Equal(expected, once);
        Assert.Equal(once, Phraseology.Normalize(once));
    }

    [Fact]
    public void UserFile_IsLaidOverTheDefaults()
    {
        var user = PronunciationTable.Parse("""
            {
              "//": "mine",
              "words": { "ECAM": "eekam", "SID": "", "APU": "ah-poo", "bad key": "x", "QRH": "kwerh" },
              "letters": [ "TOGA", "XYZ", "toolongabbreviation" ]
            }
            """);
        Assert.NotNull(user);
        var table = user!.Over(PronunciationTable.Defaults);

        Assert.Equal("eekam", table.Apply("ECAM"));          // changed
        Assert.Equal("SID", table.Apply("SID"));             // empty = as written
        Assert.Equal("ah-poo", table.Apply("APU"));          // moved from letters to words
        Assert.Equal("T O G A", table.Apply("TOGA"));        // moved from words to letters
        Assert.Equal("X Y Z", table.Apply("XYZ"));           // added
        Assert.Equal("star", table.Apply("STAR"));           // default kept
        Assert.Equal("I L S", table.Apply("ILS"));           // default kept
        Assert.DoesNotContain("bad key", table.Words.Keys);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("[1, 2]")]
    public void Parse_UnreadableText_IsNull(string? json)
        => Assert.Null(PronunciationTable.Parse(json));

    [Fact]
    public void ShippedFile_IsTheDefaultTable()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ProsimCompanion.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var shipped = PronunciationTable.Parse(File.ReadAllText(
            Path.Combine(dir!.FullName, "src", "ProsimCompanion.App", "config", PronunciationTable.FileName)));

        Assert.NotNull(shipped);
        Assert.Equal(
            PronunciationTable.Defaults.Words.OrderBy(w => w.Key, StringComparer.Ordinal),
            shipped!.Words.OrderBy(w => w.Key, StringComparer.Ordinal));
        Assert.Equal(
            PronunciationTable.Defaults.Letters.OrderBy(l => l, StringComparer.Ordinal),
            shipped.Letters.OrderBy(l => l, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("ENG 1 FIRE", "ECAM, engine 1 fire.", "engine 1 fire")]
    [InlineData("HYD B RSVR LO LVL", "ECAM, hydraulic, blue reservoir low level.", "hydraulic, blue reservoir low level")]
    [InlineData("ENG 1 FIRE", "", "ENG 1 FIRE")]
    [InlineData("ENG 1 FIRE", null, "ENG 1 FIRE")]
    public void ClearedFault_IsNamedByItsSpokenLine(string title, string? announce, string expected)
        => Assert.Equal(expected, ProsimCompanion.Speech.Abnormals.FailureMonitor.SpokenFaultName(title, announce));
}
