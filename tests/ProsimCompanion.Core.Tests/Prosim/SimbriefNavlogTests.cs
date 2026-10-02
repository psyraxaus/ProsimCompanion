using System.Text.Json.Nodes;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Prosim.Simbrief;
using ProsimCompanion.Speech.Monitoring;
using Xunit;

namespace ProsimCompanion.Core.Tests.Prosim;

/// <summary>Issue #148: the SimBrief navlog through the OFP parser, against a saved fixture
/// in the xml.fetcher json=1 shape (hand-written from the documented format, EGLL→LIRF).</summary>
public sealed class SimbriefNavlogTests
{
    private static JsonObject Fixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Prosim", "Fixtures", "simbrief-ofp-sample.json");
        return (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
    }

    [Fact]
    public void ParseOfp_CarriesTheNavlog_InRouteOrder_WithFuelTimeAndPosition()
    {
        var ofp = SimbriefImportService.ParseOfp(Fixture());

        Assert.Equal("EGLL", ofp.OriginIcao);
        Assert.Equal("LIRF", ofp.DestinationIcao);
        Assert.Equal(3050, ofp.FuelPlanLandingKg);
        Assert.Equal(9, ofp.Navlog.Count);
        Assert.Equal(["DET", "TOC", "KONAN", "KOK", "RLP", "GVA", "TOD", "ELB", "LIRF"], ofp.Navlog.Select(f => f.Ident));

        var det = ofp.Navlog[0];
        Assert.Equal(51.303889, det.Position.LatitudeDeg, 6);
        Assert.Equal(0.597222, det.Position.LongitudeDeg, 6);
        Assert.Equal(7450, det.PlannedFuelOnBoardKg);
        Assert.Equal(TimeSpan.FromMinutes(8), det.TimeFromTakeoff);
        Assert.Equal(6000, det.AltitudeFt);
        Assert.True(det.IsProcedure);
        Assert.False(ofp.Navlog[2].IsProcedure);
        // The destination row closes the plan at the planned landing fuel.
        Assert.Equal(3050, ofp.Navlog[^1].PlannedFuelOnBoardKg);
    }

    [Fact]
    public void ParseOfp_LbsPlan_ConvertsNavlogFuelToKilograms()
    {
        var fixture = Fixture();
        fixture["params"]!["units"] = "lbs";

        var ofp = SimbriefImportService.ParseOfp(fixture);

        Assert.Equal(7450 / 2.20462, ofp.Navlog[0].PlannedFuelOnBoardKg!.Value, 0.5);
    }

    [Fact]
    public void ParseNavlog_ToleratesASingleFixObject_MissingFields_AndNoNavlogAtAll()
    {
        var single = JsonNode.Parse("""{"fix":{"ident":"ONLY","pos_lat":"50.0","pos_long":"2.0"}}""");
        var noPosition = JsonNode.Parse("""{"fix":[{"ident":"GHOST","fuel_plan_onboard":"100"},{"ident":"REAL","pos_lat":"50","pos_long":"3"}]}""");

        var one = SimbriefImportService.ParseNavlog(single, Kg);
        var skipped = SimbriefImportService.ParseNavlog(noPosition, Kg);

        var only = Assert.Single(one);
        Assert.Equal("ONLY", only.Ident);
        Assert.Null(only.PlannedFuelOnBoardKg);
        Assert.Null(only.TimeFromTakeoff);
        Assert.Equal("REAL", Assert.Single(skipped).Ident);
        Assert.Empty(SimbriefImportService.ParseNavlog(null, Kg));
        Assert.Empty(SimbriefImportService.ParseNavlog(JsonNode.Parse("\"\""), Kg));
        Assert.Empty(SimbriefImportService.ParseOfp(Strip(Fixture(), "navlog")).Navlog);
    }

    [Fact]
    public void LastFixPassed_OnTheFixtureRoute()
    {
        var navlog = SimbriefImportService.ParseOfp(Fixture()).Navlog;

        // Half way between RLP and GVA, on the line.
        var between = GreatCircle.Intermediate(navlog[4].Position, navlog[5].Position, 0.5);
        var passed = FuelCheckCore.LastFixPassed(navlog, between);
        Assert.Equal(4, passed!.Value.Index);
        Assert.Equal(0.5, passed.Value.Fraction, 2);

        // Planned fuel there is interpolated between the two fixes: (5600 + 5100) / 2.
        Assert.Equal(5350, FuelCheckCore.PlannedFuelAt(navlog, passed.Value.Index, passed.Value.Fraction)!.Value, 1);

        // Right over KONAN: the leg KONAN → KOK at its start.
        var overKonan = FuelCheckCore.LastFixPassed(navlog, navlog[2].Position);
        Assert.Equal(2, overKonan!.Value.Index);
        Assert.Equal(0, overKonan.Value.Fraction, 2);
    }

    private static double Kg(JsonNode? node)
        => node is null ? 0 : double.Parse(node.ToString(), System.Globalization.CultureInfo.InvariantCulture);

    private static JsonObject Strip(JsonObject ofp, string key)
    {
        ofp.Remove(key);
        return ofp;
    }
}
