using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Airports;
using ProsimCompanion.Core.Speech;
using ProsimCompanion.Speech.FoSide;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>Airports are named, not spelled (owner request 2026-11-14: the FMGC cross-check
/// read "INIT page: EGLL to EHAM"), from a user-editable list that works without Navigraph.</summary>
public sealed class AirportNameCatalogTests
{
    private const string Json = """
        {
          "//": "comment",
          "airports": [
            { "icao": "EGLL", "name": "Heathrow", "city": "London" },
            { "icao": "EGKK", "name": "Gatwick", "city": "London" },
            { "icao": "EHAM", "name": "Schiphol", "city": "Amsterdam" },
            { "icao": "EGLC", "name": "", "city": "London" },
            { "icao": "EGLL", "name": "Second entry loses", "city": "" },
            { "icao": "XX", "name": "Bad code", "city": "" },
            { "icao": "ZZZZ", "name": "", "city": "" }
          ]
        }
        """;

    private static readonly IReadOnlyList<AdiruState> AllAligned = [new(1, false, 0, true), new(2, false, 0, true), new(3, false, 0, true)];

    [Theory]
    [InlineData("EGLL", "Heathrow")]
    [InlineData("egll", "Heathrow")]
    [InlineData(" EHAM ", "Schiphol")]
    [InlineData("EGLC", "London")]      // a blank name says the city
    [InlineData("ZZZZ", null)]
    [InlineData("XX", null)]
    [InlineData("KJFK", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void SpokenName(string? icao, string? expected)
        => Assert.Equal(expected, AirportNameCatalog.FromJson(Json).SpokenName(icao));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{ not json")]
    public void AnUnreadableFile_NamesNothing(string? json)
        => Assert.Null(AirportNameCatalog.FromJson(json).SpokenName("EGLL"));

    [Fact]
    public void Chain_TheFirstTierThatKnowsTheAirportAnswers()
    {
        var file = AirportNameCatalog.FromJson(Json);
        var database = AirportNameCatalog.FromJson("""{ "airports": [ { "icao": "EGLL", "name": "London Heathrow" }, { "icao": "KJFK", "name": "John F Kennedy" } ] }""");
        var chain = new AirportNameChain([file, database]);

        Assert.Equal("Heathrow", chain.SpokenName("EGLL"));          // the pilot's file wins
        Assert.Equal("John F Kennedy", chain.SpokenName("KJFK"));    // the database fills the gap
        Assert.Null(chain.SpokenName("ZZZZ"));
    }

    [Fact]
    public void ShippedFile_ParsesAndCarriesTheCrewShortForms()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ProsimCompanion.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var names = AirportNameCatalog.Parse(File.ReadAllText(
            Path.Combine(dir!.FullName, "src", "ProsimCompanion.App", "config", AirportNameCatalog.FileName)));

        Assert.True(names.Count > 5000);
        Assert.Equal("Heathrow", names["EGLL"]);
        Assert.Equal("Schiphol", names["EHAM"]);
        Assert.Equal("Kennedy", names["KJFK"]);
        Assert.DoesNotContain(names.Values, n => n.Contains("Airport", StringComparison.OrdinalIgnoreCase) && n.EndsWith("Airport", StringComparison.OrdinalIgnoreCase));
    }

    // ---- the FMGC cross-check read-out ----

    private static string Named(string? icao) => new SpokenText(AirportNameCatalog.FromJson(Json)).Airport(icao);

    [Fact]
    public void CrossCheck_NamesTheAirports()
    {
        var ofp = new OfpData { OriginIcao = "EGLL", DestinationIcao = "EHAM", CruiseFlightLevel = 370 };
        var readout = CrossCheckComposer.Compose(AllAligned, new FmgcFacts("EGLL", "EHAM", 37000, 142, 145, 149, 55), ofp, null, Named);

        Assert.Contains("INIT page: Heathrow to Schiphol.", readout.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("The plan says", readout.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("EGLL", readout.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void CrossCheck_AMismatchNamesBothRoutes()
    {
        var ofp = new OfpData { OriginIcao = "EGKK", DestinationIcao = "EHAM", CruiseFlightLevel = 370 };
        var readout = CrossCheckComposer.Compose(AllAligned, new FmgcFacts("EGLL", "EHAM", 37000, 142, 145, 149, 55), ofp, null, Named);

        Assert.Contains("INIT page: Heathrow to Schiphol. The plan says Gatwick to Schiphol.", readout.Text, StringComparison.Ordinal);
        Assert.Contains("route EGLL/EHAM vs plan EGKK/EHAM", readout.Mismatches);
    }

    [Fact]
    public void CrossCheck_TwoAirportsSaidTheSame_AreSpelled_SoTheMismatchIsHeard()
    {
        // EGLC is said "London" here; a file that named Heathrow "London" too would hide it.
        static string ByCity(string? icao) => icao is "EGLL" or "EGLC" ? "London" : Named(icao);
        var ofp = new OfpData { OriginIcao = "EGLC", DestinationIcao = "EHAM", CruiseFlightLevel = 370 };
        var readout = CrossCheckComposer.Compose(AllAligned, new FmgcFacts("EGLL", "EHAM", 37000, 142, 145, 149, 55), ofp, null, ByCity);

        Assert.Contains(
            "INIT page: Echo Golf Lima Lima to Echo Hotel Alpha Mike. The plan says Echo Golf Lima Charlie to Echo Hotel Alpha Mike.",
            readout.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void CrossCheck_WithNoNames_SpellsTheCode_NeverTheBareCode()
    {
        var readout = CrossCheckComposer.Compose(AllAligned, new FmgcFacts("EGLL", "LIRF", 37000, 142, 145, 149, 55), null, null);

        Assert.Contains("INIT page: Echo Golf Lima Lima to Lima India Romeo Foxtrot.", readout.Text, StringComparison.Ordinal);
    }
}
