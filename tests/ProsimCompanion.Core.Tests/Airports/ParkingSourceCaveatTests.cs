using ProsimCompanion.Core.Airports.Parking;
using Xunit;

namespace ProsimCompanion.Core.Tests.Airports;

/// <summary>Issue #156 (EFHK W36, 2026-10-11): the GSX profile names "Gate 36" but the scenery
/// has only 36A and 36B — the number alone is refused <c>ambiguous</c>. A stand known to the
/// profile only, when the scenery answered too, is a Likely match with the scenery's variants
/// as alternatives; a stand the scenery lists that the profile does not name stays Exact.</summary>
public sealed class ParkingSourceCaveatTests
{
    private static AirportParking Stand(ParkingName name, int number, string suffix, ParkingDataSources sources, string? gateName = null, string? uiName = null)
        => AirportParking.Bare(new ParkingIdentity(name, number, suffix), sources) with { GsxGateName = gateName, GsxUiName = uiName };

    private static readonly AirportParkings Efhk = new(
        "EFHK",
        [
            Stand(ParkingName.GateW, 36, "", ParkingDataSources.GsxPy, "Gate 36", "Apron 1W (Gates W34-W48) | Gate 36"),
            Stand(ParkingName.GateW, 36, "A", ParkingDataSources.Facility),
            Stand(ParkingName.GateW, 36, "B", ParkingDataSources.Facility),
            Stand(ParkingName.GateW, 40, "", ParkingDataSources.GsxPy | ParkingDataSources.Facility, "Gate 40", "Apron 1W (Gates W34-W48) | Gate 40"),
            Stand(ParkingName.Parking, 401, "", ParkingDataSources.Facility),
        ],
        ParkingDataSources.GsxPy | ParkingDataSources.Facility,
        [],
        DateTimeOffset.UnixEpoch);

    [Fact]
    public void ProfileOnlyStand_WhenTheSceneryAnswered_IsLikely_WithTheSceneryVariants()
    {
        var match = ParkingTokenResolver.Resolve(Efhk, "W36")!;

        Assert.Equal(new ParkingIdentity(ParkingName.GateW, 36, ""), match.Parking.Identity);
        Assert.Equal(ParkingMatchConfidence.Likely, match.Confidence);
        Assert.Equal("known to the GSX profile only — the scenery lists W36A, W36B", match.Caveat);
        Assert.Equal(["W36A", "W36B"], match.Alternatives.Select(a => a.Identity.Designator));
        Assert.Equal(36, match.GsxTokens[0]);
        Assert.Contains("the scenery lists W36A, W36B", ParkingDescription.Describe(match));
    }

    [Fact]
    public void StandKnownToBothSources_StaysExact()
    {
        var match = ParkingTokenResolver.Resolve(Efhk, "W40")!;

        Assert.Equal(ParkingMatchConfidence.Exact, match.Confidence);
        Assert.Null(match.Caveat);
    }

    [Fact]
    public void SceneryOnlyStand_IsNotDowngraded()
    {
        var match = ParkingTokenResolver.Resolve(Efhk, "W36B")!;

        Assert.Equal(new ParkingIdentity(ParkingName.GateW, 36, "B"), match.Parking.Identity);
        Assert.Equal(ParkingMatchConfidence.Exact, match.Confidence);
        Assert.Null(match.Caveat);
    }

    [Fact]
    public void ProfileOnlyCatalogue_HasNoSceneryToDisagreeWith()
    {
        var profileOnly = Efhk with
        {
            Parkings = Efhk.Parkings.Where(p => p.Sources.HasFlag(ParkingDataSources.GsxPy)).ToList(),
            Sources = ParkingDataSources.GsxPy,
        };

        var match = ParkingTokenResolver.Resolve(profileOnly, "W36")!;

        Assert.Equal(ParkingMatchConfidence.Exact, match.Confidence);
        Assert.Null(match.Caveat);
    }

    [Theory]
    [InlineData("Gate W 36A", ParkingName.GateW, 36, "A")]
    [InlineData(" Gate 36A", ParkingName.Gate, 36, "A")]
    [InlineData("Gate W40", ParkingName.GateW, 40, "")]
    [InlineData("Parking 401", ParkingName.Parking, 401, "")]
    [InlineData("N Parking 5", ParkingName.NParking, 5, "")]
    [InlineData("Dock 3", ParkingName.Dock, 3, "")]
    [InlineData("Stand 401", null, 401, "")]
    [InlineData("36B", null, 36, "B")]
    public void SceneryName_Parses(string text, ParkingName? name, int number, string suffix)
    {
        var parts = ParkingIdentity.TryParseSceneryName(text)!;

        Assert.Equal(name, parts.Name);
        Assert.Equal(number, parts.Number);
        Assert.Equal(suffix, parts.Suffix);
        Assert.Equal($"{number}{suffix}", parts.Designator);
    }

    [Theory]
    [InlineData("Moving Map")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Apron 1W (Gates W34-W48) | Gate 36")]
    public void NonSceneryNames_AreNull(string text) => Assert.Null(ParkingIdentity.TryParseSceneryName(text));
}
