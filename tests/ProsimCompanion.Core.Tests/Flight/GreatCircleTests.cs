using ProsimCompanion.Core.Flight;
using Xunit;

namespace ProsimCompanion.Core.Tests.Flight;

public sealed class GreatCircleTests
{
    private static readonly GeoPoint Egll = new(51.4775, -0.4614);
    private static readonly GeoPoint Kjfk = new(40.6398, -73.7789);
    private static readonly GeoPoint Lirf = new(41.8003, 12.2389);
    private static readonly GeoPoint Yssy = new(-33.9461, 151.1772);
    private static readonly GeoPoint Nzaa = new(-37.0081, 174.7917);

    [Fact]
    public void Distance_KnownPairs()
    {
        // Published great-circle figures: LHR–JFK 2,991 nm, LHR–FCO 780 nm, SYD–AKL 1,165 nm.
        Assert.Equal(2991, GreatCircle.DistanceNm(Egll, Kjfk), 0.5 / 100 * 2991);
        Assert.Equal(780, GreatCircle.DistanceNm(Egll, Lirf), 5.0);
        Assert.Equal(1165, GreatCircle.DistanceNm(Yssy, Nzaa), 6.0);
    }

    [Fact]
    public void Distance_OneDegreeOfLongitudeOnTheEquator_IsSixtyMiles()
        => Assert.Equal(60.04, GreatCircle.DistanceNm(new GeoPoint(0, 10), new GeoPoint(0, 11)), 2);

    [Fact]
    public void Distance_IsSymmetric_AndZeroForTheSamePoint()
    {
        Assert.Equal(GreatCircle.DistanceNm(Egll, Kjfk), GreatCircle.DistanceNm(Kjfk, Egll), 6);
        Assert.Equal(0, GreatCircle.DistanceNm(Egll, Egll), 9);
    }

    [Fact]
    public void Distance_CrossesTheAntimeridianTheShortWay()
        => Assert.Equal(120.08, GreatCircle.DistanceNm(new GeoPoint(0, 179), new GeoPoint(0, -179)), 1);

    [Fact]
    public void Distance_ShortTaxiLeg_KeepsItsPrecision()
    {
        // 0.001° of latitude is 0.06 nm (about 111 m) — the haversine form must not round it away.
        Assert.Equal(0.06, GreatCircle.DistanceNm(new GeoPoint(51.4775, -0.4614), new GeoPoint(51.4785, -0.4614)), 3);
    }

    [Theory]
    [InlineData(0, 0, 10, 0, 0)]      // due north
    [InlineData(0, 0, 0, 10, 90)]     // due east along the equator
    [InlineData(10, 0, 0, 0, 180)]    // due south
    [InlineData(0, 10, 0, 0, 270)]    // due west
    public void InitialBearing_Cardinals(double lat1, double lon1, double lat2, double lon2, double expected)
        => Assert.Equal(expected, GreatCircle.InitialBearingDeg(new GeoPoint(lat1, lon1), new GeoPoint(lat2, lon2)), 6);

    [Fact]
    public void InitialBearing_KnownPair()
    {
        // LHR → JFK leaves on about 288° true and arrives from the north-east.
        Assert.Equal(288, GreatCircle.InitialBearingDeg(Egll, Kjfk), 1.0);
        Assert.Equal(51, GreatCircle.InitialBearingDeg(Kjfk, Egll), 1.0);
    }

    [Fact]
    public void Intermediate_EndsAndMidpoint()
    {
        var start = GreatCircle.Intermediate(Egll, Kjfk, 0);
        var end = GreatCircle.Intermediate(Egll, Kjfk, 1);
        var mid = GreatCircle.Intermediate(Egll, Kjfk, 0.5);

        Assert.Equal(0, GreatCircle.DistanceNm(start, Egll), 3);
        Assert.Equal(0, GreatCircle.DistanceNm(end, Kjfk), 3);
        Assert.Equal(GreatCircle.DistanceNm(Egll, mid), GreatCircle.DistanceNm(mid, Kjfk), 3);
        // The midpoint of a North Atlantic great circle is well north of both ends.
        Assert.True(mid.LatitudeDeg > Egll.LatitudeDeg);
    }

    [Fact]
    public void AlongCrossTrack_OnTheLine()
    {
        var route = GreatCircle.DistanceNm(Egll, Kjfk);
        var third = GreatCircle.Intermediate(Egll, Kjfk, 1.0 / 3);

        var (along, cross) = GreatCircle.AlongCrossTrackNm(Egll, Kjfk, third);

        Assert.Equal(route / 3, along, 1);
        Assert.Equal(0, cross, 1);
    }

    [Fact]
    public void AlongCrossTrack_RightIsPositive_LeftIsNegative()
    {
        // Eastbound along the equator: south of it is to the right.
        var from = new GeoPoint(0, 0);
        var to = new GeoPoint(0, 20);

        var right = GreatCircle.AlongCrossTrackNm(from, to, new GeoPoint(-1, 10));
        var left = GreatCircle.AlongCrossTrackNm(from, to, new GeoPoint(1, 10));

        Assert.Equal(60.04, right.CrossNm, 1);
        Assert.Equal(-60.04, left.CrossNm, 1);
        Assert.Equal(600.4, right.AlongNm, 0);
    }

    [Fact]
    public void AlongCrossTrack_BehindTheStart_IsNegativeAlong()
    {
        var (along, _) = GreatCircle.AlongCrossTrackNm(new GeoPoint(0, 0), new GeoPoint(0, 20), new GeoPoint(0, -1));

        Assert.Equal(-60.04, along, 1);
    }

    [Fact]
    public void AlongCrossTrack_DegenerateInputs_DoNotProduceNaN()
    {
        var atStart = GreatCircle.AlongCrossTrackNm(Egll, Kjfk, Egll);
        var noRoute = GreatCircle.AlongCrossTrackNm(Egll, Egll, Lirf);

        Assert.Equal((0, 0), atStart);
        Assert.Equal(0, noRoute.AlongNm);
        Assert.True(double.IsFinite(noRoute.CrossNm));
    }

    [Theory]
    [InlineData(51.4775, -0.4614, true)]
    [InlineData(0, 0, false)]                 // the unpopulated-dataref signature
    [InlineData(0, 12.5, true)]               // on the equator is a real place
    [InlineData(48.1, 0, true)]               // so is the Greenwich meridian
    [InlineData(double.NaN, 10, false)]
    [InlineData(10, double.NaN, false)]
    [InlineData(double.PositiveInfinity, 10, false)]
    [InlineData(91, 10, false)]
    [InlineData(10, 181, false)]
    public void GeoPoint_FromRaw_AcceptsOnlyARealPosition(double lat, double lon, bool valid)
        => Assert.Equal(valid, GeoPoint.FromRaw(lat, lon) is not null);
}
