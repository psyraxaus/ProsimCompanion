namespace ProsimCompanion.Core.Flight;

/// <summary>A position on the earth in decimal degrees (north / east positive).</summary>
public readonly record struct GeoPoint(double LatitudeDeg, double LongitudeDeg)
{
    /// <summary>
    /// The one "is this a position?" rule (issue #145): NaN / infinite, out of range, or
    /// exactly (0, 0) is "no position". (0, 0) is what an unpopulated dataref and a ProSim
    /// with no sim attached both read as — a point in the Gulf of Guinea no A322 leg starts
    /// from — so it is treated as absent rather than as 2,500 nm of bogus distance flown.
    /// </summary>
    public static GeoPoint? FromRaw(double latitudeDeg, double longitudeDeg)
    {
        if (!double.IsFinite(latitudeDeg) || !double.IsFinite(longitudeDeg))
        {
            return null;
        }

        if (Math.Abs(latitudeDeg) > 90 || Math.Abs(longitudeDeg) > 180)
        {
            return null;
        }

        if (latitudeDeg == 0 && longitudeDeg == 0)
        {
            return null;
        }

        return new GeoPoint(latitudeDeg, longitudeDeg);
    }
}
