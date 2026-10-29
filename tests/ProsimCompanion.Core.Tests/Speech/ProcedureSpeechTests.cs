using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.Speech;
using ProsimCompanion.Speech.SayIntentions;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>Owner request 2026-10-29 (EGLL, BPK7F): the FO spelled the departure letter by
/// letter where a crew says "Brookmans Park seven Foxtrot"; the same for the STAR.</summary>
public sealed class ProcedureSpeechTests
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BPK"] = "BROOKMANS PARK",
        ["LAM"] = "LAMBOURNE",
        ["DET"] = "DETLING",
        ["KEMA"] = "KEMA",   // a 5LNC-style waypoint whose name repeats the ident
        ["RATEV"] = "RATEV",
    };

    private static string? NameOf(string ident) => Names.TryGetValue(ident, out var name) ? name : null;

    [Theory]
    [InlineData("BPK7F", "Brookmans Park seven Foxtrot")]
    [InlineData("bpk7f", "Brookmans Park seven Foxtrot")]
    [InlineData("LAM2X", "Lambourne two X-ray")]
    [InlineData("DET1J", "Detling one Juliet")]
    [InlineData("KEMA5C", "Kema five Charlie")]
    [InlineData("RATEV3A", "Ratev three Alpha")]
    [InlineData("FISHA1", "Fisha one")]          // unnamed, 4+ letters: a word, title-cased for the TTS
    [InlineData("WOD1A", "Whiskey Oscar Delta one Alpha")] // unnamed, short: NATO letters, never bare
    [InlineData("LOGAN2H", "Logan two Hotel")]
    [InlineData("ILS09L", "ILS zero niner Lima")] // not procedure-shaped: the phonetic speller
    [InlineData("", "")]
    [InlineData("  ", "")]
    public void Speak_SaysTheCrewsName_ThenNumberAndDesignator(string identifier, string expected)
        => Assert.Equal(expected, ProcedureSpeech.Speak(identifier, NameOf));

    [Fact]
    public void SpokenText_Procedure_UsesTheFixNames_AndFallsBackWithout()
    {
        var withNames = new SpokenText(null, new FakeFixNames());
        var without = new SpokenText();

        Assert.Equal("Brookmans Park seven Foxtrot", withNames.Procedure("BPK7F", "EGLL"));
        Assert.Equal("Bravo Papa Kilo seven Foxtrot", without.Procedure("BPK7F"));
        Assert.Equal("", without.Procedure(null));
    }

    [Fact]
    public void OfpFixNames_ReadsTheNavlog_ProcedureFixFirst_NeverTheIdentItself()
    {
        var store = new OfpStore();
        store.Set(new OfpData
        {
            Navlog =
            [
                new OfpFix("BPK", new GeoPoint(51.75, -0.11), null, null, null, IsProcedure: true, Name: "BROOKMANS PARK"),
                new OfpFix("KONAN", new GeoPoint(50.37, 2.56), null, null, null, IsProcedure: false, Name: "KONAN"),
                new OfpFix("TOC", new GeoPoint(50.6, 2.1), null, null, null, IsProcedure: false, Name: "TOP OF CLIMB"),
            ],
        });
        var names = new OfpFixNames(store);

        Assert.Equal("BROOKMANS PARK", names.Name("BPK", "EGLL"));
        Assert.Equal("BROOKMANS PARK", names.Name("bpk", null));
        Assert.Null(names.Name("KONAN", null)); // the name only repeats the ident
        Assert.Null(names.Name("LAM", null));
        Assert.Null(names.Name("", null));

        store.Clear();
        Assert.Null(names.Name("BPK", null));
    }

    [Fact]
    public void CheckIn_UsesTheSpokenProcedureName_WhenGiven()
    {
        var context = new CheckInContext("Good morning", "Speedbird-eight-five-zero", FlightPhase.Climb, false, 18_040, "FL210",
            "BPK7F", "LOGAN2H", "27R", "24", "311", SidSpoken: "Brookmans Park seven Foxtrot", StarSpoken: "Logan two Hotel");

        Assert.Equal("Good morning, Speedbird-eight-five-zero, climbing through FL180 for FL210 on Brookmans Park seven Foxtrot.",
            CheckInComposer.Compose(context)!.Transmit);
        Assert.Equal("Good morning, Speedbird-eight-five-zero, descending through FL200 for FL120 on Logan two Hotel.",
            CheckInComposer.Compose(context with { Phase = FlightPhase.Descent, AltitudeFt = 20_100, ClearedLevel = "FL120" })!.Transmit);
    }

    private sealed class FakeFixNames : IFixNames
    {
        public string? Name(string ident, string? nearAirportIcao) => NameOf(ident);
    }
}
