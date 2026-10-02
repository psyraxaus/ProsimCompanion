namespace ProsimCompanion.Core.Flight;

/// <summary>
/// Great-circle maths on a spherical earth (issue #145). Pure; every angle in degrees, every
/// distance in nautical miles. The sphere is accurate to about 0.3 % against the WGS-84
/// ellipsoid — far inside what a progress line, an ETA and a 3:1 descent estimate need.
/// </summary>
public static class GreatCircle
{
    /// <summary>Mean earth radius (6 371.0 km) in nautical miles.</summary>
    public const double EarthRadiusNm = 3440.065;

    /// <summary>Shortest surface distance between two points (haversine — stable for the
    /// short distances a taxiing aircraft produces, where the cosine form loses precision).</summary>
    public static double DistanceNm(GeoPoint from, GeoPoint to)
        => AngularDistance(from, to) * EarthRadiusNm;

    /// <summary>True bearing at <paramref name="from"/> of the great circle to
    /// <paramref name="to"/>, 0–360. Coincident points read 0.</summary>
    public static double InitialBearingDeg(GeoPoint from, GeoPoint to)
        => ToDegrees(Normalize(BearingRad(from, to)));

    /// <summary>The point <paramref name="fraction"/> (0–1) of the way along the great
    /// circle from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public static GeoPoint Intermediate(GeoPoint from, GeoPoint to, double fraction)
    {
        var d = AngularDistance(from, to);
        if (d < 1e-12)
        {
            return from;
        }

        var f = Math.Clamp(fraction, 0, 1);
        var a = Math.Sin((1 - f) * d) / Math.Sin(d);
        var b = Math.Sin(f * d) / Math.Sin(d);
        var (lat1, lon1) = (ToRadians(from.LatitudeDeg), ToRadians(from.LongitudeDeg));
        var (lat2, lon2) = (ToRadians(to.LatitudeDeg), ToRadians(to.LongitudeDeg));
        var x = (a * Math.Cos(lat1) * Math.Cos(lon1)) + (b * Math.Cos(lat2) * Math.Cos(lon2));
        var y = (a * Math.Cos(lat1) * Math.Sin(lon1)) + (b * Math.Cos(lat2) * Math.Sin(lon2));
        var z = (a * Math.Sin(lat1)) + (b * Math.Sin(lat2));
        return new GeoPoint(
            ToDegrees(Math.Atan2(z, Math.Sqrt((x * x) + (y * y)))),
            ToDegrees(Math.Atan2(y, x)));
    }

    /// <summary>
    /// Where <paramref name="point"/> sits relative to the great circle
    /// <paramref name="from"/> → <paramref name="to"/>: the distance along it from
    /// <paramref name="from"/> to the abeam point (negative when behind the start), and the
    /// distance off it (positive = right of track). The route strip's two axes.
    /// </summary>
    public static (double AlongNm, double CrossNm) AlongCrossTrackNm(GeoPoint from, GeoPoint to, GeoPoint point)
    {
        var d13 = AngularDistance(from, point);
        if (d13 < 1e-12)
        {
            return (0, 0);
        }

        if (AngularDistance(from, to) < 1e-12)
        {
            // No track to measure against: everything is "off" it.
            return (0, d13 * EarthRadiusNm);
        }

        var delta = BearingRad(from, point) - BearingRad(from, to);
        var cross = Math.Asin(Math.Clamp(Math.Sin(d13) * Math.Sin(delta), -1, 1));
        var along = Math.Acos(Math.Clamp(Math.Cos(d13) / Math.Cos(cross), -1, 1));
        if (Math.Cos(delta) < 0)
        {
            along = -along;
        }

        return (along * EarthRadiusNm, cross * EarthRadiusNm);
    }

    private static double AngularDistance(GeoPoint from, GeoPoint to)
    {
        var lat1 = ToRadians(from.LatitudeDeg);
        var lat2 = ToRadians(to.LatitudeDeg);
        var dLat = lat2 - lat1;
        var dLon = ToRadians(to.LongitudeDeg - from.LongitudeDeg);
        var h = (Math.Sin(dLat / 2) * Math.Sin(dLat / 2))
            + (Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2));
        return 2 * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }

    private static double BearingRad(GeoPoint from, GeoPoint to)
    {
        var lat1 = ToRadians(from.LatitudeDeg);
        var lat2 = ToRadians(to.LatitudeDeg);
        var dLon = ToRadians(to.LongitudeDeg - from.LongitudeDeg);
        var y = Math.Sin(dLon) * Math.Cos(lat2);
        var x = (Math.Cos(lat1) * Math.Sin(lat2)) - (Math.Sin(lat1) * Math.Cos(lat2) * Math.Cos(dLon));
        return Math.Atan2(y, x);
    }

    private static double Normalize(double radians)
    {
        var twoPi = 2 * Math.PI;
        var value = radians % twoPi;
        return value < 0 ? value + twoPi : value;
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180.0;

    private static double ToDegrees(double radians) => radians * 180.0 / Math.PI;
}
