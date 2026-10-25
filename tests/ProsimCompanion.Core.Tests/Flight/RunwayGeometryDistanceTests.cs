using ProsimCompanion.Core.Airports;
using ProsimCompanion.Core.Flight;
using Xunit;

namespace ProsimCompanion.Core.Tests.Flight;

/// <summary>Owner flight 2026-10-25, EKCH 22R: the aircraft held short at intersection A1 about
/// 1 nm down the runway, the 0.3 nm "from the threshold" rule never tripped and the SI copilot
/// kept the radios. The holding point is now measured to the runway strip.</summary>
public sealed class RunwayGeometryDistanceTests
{
    // EKCH 22R-ish: threshold at the north-east end, 221° true, 3 600 m (11 811 ft).
    private static readonly GeoPoint Threshold = new(55.6286, 12.6568);
    private static readonly RunwayGeometry Runway22R = new("EKCH", "22R", Threshold, 221.0, 11_811, "test");

    [Fact]
    public void Destination_ReturnsAlongTheBearing()
    {
        var farEnd = GreatCircle.Destination(Threshold, 221.0, 11_811 / 6076.12);

        Assert.InRange(GreatCircle.DistanceNm(Threshold, farEnd), 1.93, 1.96);
        Assert.InRange(GreatCircle.InitialBearingDeg(Threshold, farEnd), 220.5, 221.5);
    }

    [Fact]
    public void HoldingShortAtAnIntersection_IsCloseToTheRunway_FarFromTheThreshold()
    {
        // 1 nm down the runway, 0.12 nm off the centreline (the A1 holding point).
        var abeam = GreatCircle.Destination(Threshold, 221.0, 1.0);
        var holdingPoint = GreatCircle.Destination(abeam, 221.0 + 90.0, 0.12);

        Assert.InRange(GreatCircle.DistanceNm(holdingPoint, Threshold), 0.95, 1.05);
        Assert.InRange(Runway22R.DistanceToRunwayNm(holdingPoint), 0.10, 0.14);
    }

    [Fact]
    public void BeforeTheThreshold_AndPastTheFarEnd_MeasureToTheNearerEnd()
    {
        var before = GreatCircle.Destination(Threshold, 221.0 - 180.0, 0.5);
        var beyond = GreatCircle.Destination(Threshold, 221.0, 1.95 + 0.5);

        Assert.InRange(Runway22R.DistanceToRunwayNm(before), 0.48, 0.52);
        Assert.InRange(Runway22R.DistanceToRunwayNm(beyond), 0.48, 0.52);
    }

    [Fact]
    public void WithoutHeadingOrLength_TheThresholdIsTheRunway()
    {
        var pointOnly = new RunwayGeometry("EKCH", "22R", Threshold, null, null, "test");
        var abeam = GreatCircle.Destination(Threshold, 221.0, 1.0);

        Assert.InRange(pointOnly.DistanceToRunwayNm(abeam), 0.98, 1.02);
    }
}
