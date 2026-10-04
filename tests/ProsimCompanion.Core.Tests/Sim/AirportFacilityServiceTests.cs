using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.Core.Airports.Parking;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Sim.Facilities;
using Xunit;

namespace ProsimCompanion.Core.Tests.Sim;

/// <summary>The facility-data tier (Option B, 2026-10-04): rows → stands, and the request
/// plumbing without a simulator.</summary>
public sealed class AirportFacilityServiceTests
{
    private static readonly FacilityAirportRow Efhk = new() { Latitude = 60.3172, Longitude = 24.9633, AltitudeM = 55, TaxiParkingCount = 2, JetwayCount = 1 };

    [Fact]
    public void ToParkings_BuildsIdentityPoseRadiusAndJetway()
    {
        var rows = new[]
        {
            new FacilityParkingRow { Type = 10, Name = (int)ParkingName.GateW, Suffix = 0, Number = 40, HeadingDeg = 137.5f, RadiusM = 30f, BiasX = -300f, BiasZ = -180f },
            new FacilityParkingRow { Type = 5, Name = (int)ParkingName.Parking, Suffix = 2, Number = 401, HeadingDeg = 90f, RadiusM = 20f, BiasX = 500f, BiasZ = 200f },
            new FacilityParkingRow { Type = 13, Name = 99, Number = 1 }, // not a parking name
        };
        var jetways = new[] { new FacilityJetwayRow { ParkingGate = (int)ParkingName.GateW, ParkingSuffix = 0, ParkingSpot = 40 } };

        var parkings = AirportFacilityService.ToParkings(Efhk, rows, jetways);

        Assert.Equal(2, parkings.Count);
        var w40 = parkings[0];
        Assert.Equal(new ParkingIdentity(ParkingName.GateW, 40, ""), w40.Identity);
        Assert.Equal(10, w40.TypeCode);
        Assert.Equal(30, w40.RadiusM);
        Assert.True(w40.HasJetway);
        Assert.Equal(137.5, w40.Pose!.Value.HeadingDeg, 1);
        Assert.True(w40.Pose.Value.Position.LatitudeDeg < Efhk.Latitude);   // south
        Assert.True(w40.Pose.Value.Position.LongitudeDeg < Efhk.Longitude); // west
        Assert.Equal(ParkingDataSources.Facility, w40.Sources);

        var s401 = parkings[1];
        Assert.Equal("B", s401.Identity.Suffix);
        Assert.False(s401.HasJetway);
    }

    [Fact]
    public void ToParkings_NoJetwayRows_LeavesJetwayUnknown()
    {
        var rows = new[] { new FacilityParkingRow { Name = (int)ParkingName.Gate, Number = 17, RadiusM = 20f } };
        var parking = AirportFacilityService.ToParkings(Efhk, rows, []).Single();
        Assert.Null(parking.HasJetway);
    }

    [Fact]
    public void ToParkings_NoAirportRow_HasNoPose()
    {
        var rows = new[] { new FacilityParkingRow { Name = (int)ParkingName.Gate, Number = 17 } };
        Assert.Null(AirportFacilityService.ToParkings(null, rows, []).Single().Pose);
    }

    [Fact]
    public void Offset_MovesEastAndNorth()
    {
        var origin = new GeoPoint(60.0, 25.0);
        var moved = AirportFacilityService.Offset(origin, 1000, 1000);
        Assert.InRange(GreatCircle.DistanceNm(origin, moved) * 1852.0, 1400, 1430);
        Assert.InRange(GreatCircle.InitialBearingDeg(origin, moved), 44, 46);
    }

    [Fact]
    public async Task LoadAsync_WithoutSim_IsNull()
    {
        var service = new AirportFacilityService(NullLogger<AirportFacilityService>.Instance);
        Assert.False(service.IsAvailable);
        Assert.Null(await service.LoadAsync("EFHK"));
    }

    [Fact]
    public async Task LoadAsync_RowsThenEnd_ReturnsParkings()
    {
        var service = new AirportFacilityService(NullLogger<AirportFacilityService>.Instance);
        uint captured = 0;
        service.Attach((icao, id) => { captured = id; return true; });

        var task = service.LoadAsync("EFHK");
        service.OnRow(captured, Efhk);
        service.OnRow(captured, new FacilityParkingRow { Name = (int)ParkingName.GateW, Number = 40, RadiusM = 30f });
        service.OnRow(captured, new FacilityJetwayRow { ParkingGate = (int)ParkingName.GateW, ParkingSpot = 40 });
        service.OnEnd(captured);

        var result = await task;
        Assert.NotNull(result);
        Assert.Single(result.Parkings);
        Assert.Equal("1 parkings, 1 jetways", result.Note);
        Assert.True(captured >= AirportFacilityService.RequestIdBase);
    }

    [Fact]
    public async Task LoadAsync_Detach_FailsPendingToNull()
    {
        var service = new AirportFacilityService(NullLogger<AirportFacilityService>.Instance);
        service.Attach((_, _) => true);
        var task = service.LoadAsync("EFHK");
        service.Detach();
        Assert.Null(await task);
    }

    [Fact]
    public async Task LoadAsync_Failed_IsNull_AndDefinitionFailureSilencesTheTier()
    {
        var service = new AirportFacilityService(NullLogger<AirportFacilityService>.Instance);
        uint captured = 0;
        service.Attach((_, id) => { captured = id; return true; });
        var task = service.LoadAsync("EFHK");
        service.OnFailed(captured, "DATA_ERROR");
        Assert.Null(await task);

        service.MarkDefinitionFailed();
        Assert.False(service.IsAvailable);
        Assert.Null(await service.LoadAsync("EGLL"));
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(1, "A")]
    [InlineData(2, "B")]
    [InlineData(26, "Z")]
    [InlineData(27, "")]
    public void SuffixLetter(int code, string expected) => Assert.Equal(expected, AirportFacilityService.SuffixLetter(code));
}
