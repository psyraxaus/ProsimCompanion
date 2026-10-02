using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProsimCompanion.Core.Aircraft.Gateway;
using ProsimCompanion.Core.Airports;
using ProsimCompanion.Core.Flight;
using Xunit;

namespace ProsimCompanion.Core.Tests.Airports;

public sealed class AirportCoordinatesTests
{
    private static RunwayResponse Runway(LatLng? lat, LatLng? lng, string? elevation = "83")
        => new() { RunwayId = "09L", Lat = lat, Lng = lng, ElevationFt = elevation };

    private static LatLng Point(double latitude, double longitude) => new() { Latitude = latitude, Longitude = longitude };

    // ---- gateway reduction ----

    [Fact]
    public void FromRunways_TwoPointsPerRunway_GivesTheCentreAndMeanElevation()
    {
        var runways = new[]
        {
            Runway(Point(51.4775, -0.4850), Point(51.4775, -0.4340), "79"),
            Runway(Point(51.4647, -0.4820), Point(51.4647, -0.4340), "77"),
        };

        var location = GatewayAirportCoordinates.FromRunways("EGLL", runways);

        Assert.NotNull(location);
        Assert.Equal("EGLL", location.Icao);
        Assert.Equal("gateway", location.Source);
        Assert.Equal(51.4711, location.Position.LatitudeDeg, 3);
        Assert.Equal(-0.4588, location.Position.LongitudeDeg, 3);
        Assert.Equal(78, location.ElevationFt);
    }

    [Fact]
    public void FromRunways_SplitShape_LatitudeInOnePairLongitudeInTheOther_IsOnePoint()
    {
        // The other reading of the unverified DTO: "lat" carries only a latitude, "lng" only
        // a longitude. Reading them as two points would put the airport on the equator.
        var runways = new[] { Runway(Point(51.4775, 0), Point(0, -0.4614)) };

        var location = GatewayAirportCoordinates.FromRunways("EGLL", runways);

        Assert.NotNull(location);
        Assert.Equal(51.4775, location.Position.LatitudeDeg, 4);
        Assert.Equal(-0.4614, location.Position.LongitudeDeg, 4);
    }

    [Fact]
    public void FromRunways_NoCoordinates_IsNull()
    {
        Assert.Null(GatewayAirportCoordinates.FromRunways("EGLL", null));
        Assert.Null(GatewayAirportCoordinates.FromRunways("EGLL", []));
        Assert.Null(GatewayAirportCoordinates.FromRunways("EGLL", [Runway(null, null)]));
        Assert.Null(GatewayAirportCoordinates.FromRunways("EGLL", [Runway(Point(0, 0), Point(0, 0))]));
    }

    [Fact]
    public void FromRunways_PointsSpreadAcrossTheMap_AreDistrusted()
    {
        // A wire shape this reduction does not understand must degrade to the next tier,
        // never publish a centre somewhere between the two.
        var runways = new[] { Runway(Point(51.47, -0.46), Point(40.64, -73.78)) };

        Assert.Null(GatewayAirportCoordinates.FromRunways("EGLL", runways));
    }

    [Fact]
    public void FromRunways_AcrossTheAntimeridian_AveragesTheShortWay()
    {
        var runways = new[] { Runway(Point(-16.69, 179.95), Point(-16.69, -179.95)) };

        var location = GatewayAirportCoordinates.FromRunways("NFNM", runways);

        Assert.NotNull(location);
        Assert.Equal(180, Math.Abs(location.Position.LongitudeDeg), 1);
    }

    [Fact]
    public void FromRunways_ElevationText_IsTolerant()
    {
        var withUnit = GatewayAirportCoordinates.FromRunways("EGLL", [Runway(Point(51.47, -0.46), null, "83 ft")]);
        var missing = GatewayAirportCoordinates.FromRunways("EGLL", [Runway(Point(51.47, -0.46), null, null)]);

        Assert.Equal(83, withUnit!.ElevationFt);
        Assert.Null(missing!.ElevationFt);
    }

    [Fact]
    public async Task Gateway_Unreachable_IsNull_WithoutAskingForRunways()
    {
        var gateway = new Mock<IProsimGateway>();
        gateway.Setup(g => g.IsReachableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var source = new GatewayAirportCoordinates(gateway.Object, NullLogger<GatewayAirportCoordinates>.Instance);

        Assert.Null(await source.FindAsync("EGLL"));
        gateway.Verify(g => g.GetRunwaysAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Gateway_Throwing_IsNull()
    {
        var gateway = new Mock<IProsimGateway>();
        gateway.Setup(g => g.IsReachableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        gateway.Setup(g => g.GetRunwaysAsync("EGLL", false, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("boom"));
        var source = new GatewayAirportCoordinates(gateway.Object, NullLogger<GatewayAirportCoordinates>.Instance);

        Assert.Null(await source.FindAsync("egll"));
    }

    // ---- locator: gateway first, DFD next, none = degrade ----

    private static IAirportCoordinateSource Source(string name, int order, AirportLocation? answer, Action? onCall = null)
    {
        var source = new Mock<IAirportCoordinateSource>();
        source.SetupGet(s => s.Name).Returns(name);
        source.SetupGet(s => s.Order).Returns(order);
        source.Setup(s => s.FindAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => onCall?.Invoke())
            .ReturnsAsync(answer);
        return source.Object;
    }

    private static AirportLocation At(string source) => new("EGLL", new GeoPoint(51.47, -0.46), 83, source);

    [Fact]
    public async Task Locator_AsksInOrder_RegardlessOfRegistrationOrder_AndFirstAnswerWins()
    {
        var dfdAsked = false;
        var locator = new AirportLocator(
            [Source("dfd", 20, At("dfd"), () => dfdAsked = true), Source("gateway", 10, At("gateway"))],
            NullLogger<AirportLocator>.Instance);

        var location = await locator.FindAsync(" egll ");

        Assert.Equal("gateway", location!.Source);
        Assert.False(dfdAsked);
    }

    [Fact]
    public async Task Locator_FallsThroughToTheNextTier()
    {
        var locator = new AirportLocator(
            [Source("gateway", 10, null), Source("dfd", 20, At("dfd"))],
            NullLogger<AirportLocator>.Instance);

        Assert.Equal("dfd", (await locator.FindAsync("EGLL"))!.Source);
    }

    [Fact]
    public async Task Locator_NoSources_NoAnswer_BlankIcao_AreAllNull()
    {
        var empty = new AirportLocator([], NullLogger<AirportLocator>.Instance);
        var unknown = new AirportLocator([Source("gateway", 10, null)], NullLogger<AirportLocator>.Instance);

        Assert.Null(await empty.FindAsync("EGLL"));
        Assert.Null(await unknown.FindAsync("EGLL"));
        Assert.Null(await unknown.FindAsync("  "));
        Assert.Null(await unknown.FindAsync(null));
    }

    [Fact]
    public async Task Locator_AThrowingSource_DoesNotTakeTheNextOneDown()
    {
        var broken = new Mock<IAirportCoordinateSource>();
        broken.SetupGet(s => s.Name).Returns("gateway");
        broken.SetupGet(s => s.Order).Returns(10);
        broken.Setup(s => s.FindAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        var locator = new AirportLocator([broken.Object, Source("dfd", 20, At("dfd"))], NullLogger<AirportLocator>.Instance);

        Assert.Equal("dfd", (await locator.FindAsync("EGLL"))!.Source);
    }
}
