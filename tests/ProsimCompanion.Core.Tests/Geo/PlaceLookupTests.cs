using System.Text.Json;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.Geo;
using Xunit;

namespace ProsimCompanion.Core.Tests.Geo;

/// <summary>Issue #153: the offline atlas answers "what are we flying over?" — the shipped
/// data resolves real places, the lookup picks the right country / region / sea and towns,
/// and the spoken line reads the way a First Officer would say it.</summary>
public sealed class PlaceLookupTests
{
    private static readonly Lazy<PlaceLookup> Shipped = new(() => new PlaceLookup(Atlas.Default));

    [Fact]
    public void ShippedAtlas_Loads_WithEverySectionPopulated()
    {
        var atlas = Atlas.Default;

        Assert.InRange(atlas.Countries.Count, 170, 200);
        Assert.InRange(atlas.Seas.Count, 100, 130);
        Assert.InRange(atlas.Regions.Count, 350, 450);
        Assert.InRange(atlas.Towns.Count, 20_000, 26_000);
        Assert.Contains(atlas.Countries, c => c.Name == "France" && c.Iso2 == "FR");
        Assert.Contains(atlas.Towns, t => t.Name == "Paris" && t.Rank == TownRank.Capital);
    }

    [Fact]
    public void OverOrleans_IsCentralFrance_SouthOfParis()
    {
        // 15 nm south of Orléans (47.65 N, 1.90 E) — the middle third of France north to south
        // (41–51 N) and west to east; Paris bears ~015 at ~75 nm, Orléans due north.
        var fix = Shipped.Value.Locate(new GeoPoint(47.65, 1.90), headingDeg: 180);

        Assert.Equal("France", fix.Country!.Name);
        Assert.Equal("central", fix.CountryPart);
        Assert.Null(fix.Sea);
        Assert.Equal("Paris", fix.Reference!.Town.Name);
        Assert.InRange(fix.Reference.DistanceNm, 70, 80);
        Assert.Equal("behind", fix.Reference.Side);
        Assert.Equal("Orléans", fix.Nearest!.Town.Name);
        Assert.InRange(fix.Nearest.DistanceNm, 13, 17);
        Assert.Equal("behind", fix.Nearest.Side);

        var spoken = PlaceFixText.Spoken(fix);
        Assert.StartsWith("We're over central France, about 70 miles south", spoken, StringComparison.Ordinal);
        Assert.Contains("of Paris.", spoken, StringComparison.Ordinal);
        Assert.Contains("Nearest town is Orléans, 15 miles behind us.", spoken, StringComparison.Ordinal);
    }

    [Fact]
    public void OverTheAlps_NamesTheRangeAndTheCountry()
    {
        // Above Zermatt, Switzerland: 46.02 N, 7.75 E.
        var fix = Shipped.Value.Locate(new GeoPoint(46.02, 7.75), headingDeg: null);

        Assert.Equal("Switzerland", fix.Country!.Name);
        Assert.Equal("Alps", fix.Region!.Name);
        Assert.StartsWith("We're over the Alps in Switzerland", PlaceFixText.Spoken(fix), StringComparison.Ordinal);
    }

    [Fact]
    public void OverTheNorthSea_NamesTheSea_AndTheNearestCoastCity()
    {
        // 57.0 N, -0.5 E — North Sea off Scotland, Aberdeen ~60 nm to the west.
        var fix = Shipped.Value.Locate(new GeoPoint(57.0, -0.5), headingDeg: 90);

        Assert.Null(fix.Country);
        Assert.Equal("North Sea", fix.Sea!.Name);
        Assert.StartsWith("We're over the North Sea", PlaceFixText.Spoken(fix), StringComparison.Ordinal);
        Assert.Equal("Aberdeen", (fix.Reference ?? fix.Nearest)!.Town.Name);
        Assert.Contains("of Aberdeen", PlaceFixText.Spoken(fix), StringComparison.Ordinal);
    }

    [Fact]
    public void MidAtlantic_HasNothingNearby_AndSaysSo()
    {
        var fix = Shipped.Value.Locate(new GeoPoint(45.0, -35.0), headingDeg: 270);

        Assert.Null(fix.Country);
        Assert.Equal("North Atlantic Ocean", fix.Sea!.Name);
        Assert.Null(fix.Reference);
        Assert.Null(fix.Nearest);
        Assert.Equal("We're over the North Atlantic Ocean.", PlaceFixText.Spoken(fix));
    }

    [Fact]
    public void RightOverACapital_SaysSo()
    {
        var fix = Shipped.Value.Locate(new GeoPoint(52.37, 4.90), headingDeg: 0);   // Amsterdam

        Assert.Equal("Netherlands", fix.Country!.Name);
        Assert.Null(fix.CountryPart);                                               // too small for "northern"
        Assert.StartsWith("We're right over Amsterdam, in the Netherlands.", PlaceFixText.Spoken(fix), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 0, "ahead")]
    [InlineData(90, 0, "right")]
    [InlineData(180, 0, "behind")]
    [InlineData(270, 0, "left")]
    [InlineData(10, 350, "ahead")]
    [InlineData(300, 90, "behind")]
    public void Side_IsRelativeToTheHeading(double bearing, double heading, string expected)
        => Assert.Equal(expected, PlaceLookup.Side(bearing, heading));

    [Theory]
    [InlineData(0.4, "1 mile")]
    [InlineData(7.6, "8 miles")]
    [InlineData(23, "25 miles")]
    [InlineData(64, "60 miles")]
    public void Miles_RoundTheWayAPilotSaysThem(double nm, string expected)
        => Assert.Equal(expected, PlaceFixText.Miles(nm));

    [Theory]
    [InlineData(320, "south-east")]   // the town bears 320 → we are south-east of it
    [InlineData(0, "south")]
    [InlineData(90, "west")]
    public void CompassFrom_IsWhereWeAre_RelativeToTheTown(double bearingToTown, string expected)
        => Assert.Equal(expected, PlaceFixText.CompassFrom(bearingToTown));

    [Theory]
    [InlineData("Alps", "Range/mtn", "the Alps")]
    [InlineData("Sahara", "Desert", "the Sahara")]
    [InlineData("North Sea", "Sea", "the North Sea")]
    [InlineData("Sicily", "Island", "Sicily")]
    [InlineData("Florida", "Pen/cape", "Florida")]
    [InlineData("Arabian Peninsula", "Pen/cape", "the Arabian Peninsula")]
    [InlineData("Balkans", "Pen/cape", "the Balkans")]
    [InlineData("Lapland", "Geoarea", "Lapland")]
    [InlineData("Central America", "Isthmus", "Central America")]
    public void WithArticle_AddsTheForFeatures(string name, string kind, string expected)
        => Assert.Equal(expected, PlaceFixText.WithArticle(new AtlasArea(name, kind, [])));

    [Fact]
    public void Polygon_WithAHole_ExcludesTheHole()
    {
        // A 10×10 square with a 2×2 hole in the middle.
        var polygon = new AtlasPolygon(
        [
            [0, 0, 10, 0, 10, 10, 0, 10],
            [4, 4, 6, 4, 6, 6, 4, 6],
        ]);

        Assert.True(polygon.Contains(new GeoPoint(1, 1)));
        Assert.False(polygon.Contains(new GeoPoint(5, 5)));
        Assert.False(polygon.Contains(new GeoPoint(11, 5)));
    }

    [Fact]
    public void SmallerCountry_WinsInsideALargerOutline_WithoutAHole()
    {
        var atlas = new Atlas(
            [
                new AtlasCountry("Big", "BG", "", [new AtlasPolygon([[0, 0, 10, 0, 10, 10, 0, 10]])]),
                new AtlasCountry("Enclave", "EN", "", [new AtlasPolygon([[4, 4, 6, 4, 6, 6, 4, 6]])]),
            ],
            [], [], []);

        var fix = new PlaceLookup(atlas).Locate(new GeoPoint(5, 5), null);

        Assert.Equal("Enclave", fix.Country!.Name);
    }

    [Fact]
    public void Load_ReadsTheJsonShape()
    {
        var json = JsonSerializer.Serialize(new
        {
            countries = new[] { new { n = "Testland", a2 = "TL", sub = "Nowhere", p = new[] { new[] { new[] { 0.0, 0, 10, 0, 10, 10, 0, 10 } } } } },
            seas = Array.Empty<object>(),
            regions = new[] { new { n = "Test Hills", k = "Range/mtn", p = new[] { new[] { new[] { 2.0, 2, 4, 2, 4, 4, 2, 4 } } } } },
            towns = new[] { new object[] { "Testville", "TL", 3.0, 3.0, 120000, 2 } },
        });
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));

        var atlas = Atlas.Load(stream);
        var fix = new PlaceLookup(atlas).Locate(new GeoPoint(3.0, 3.0), 0);

        Assert.Equal("Testland", fix.Country!.Name);
        Assert.Equal("Test Hills", fix.Region!.Name);
        Assert.Equal("Testville", fix.Reference!.Town.Name);
        Assert.Equal(TownRank.Capital, fix.Reference.Town.Rank);
        Assert.Equal("We're right over Testville, in south-western Testland.", PlaceFixText.Spoken(fix));
    }
}
