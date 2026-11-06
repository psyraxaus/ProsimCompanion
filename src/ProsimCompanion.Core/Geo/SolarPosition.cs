namespace ProsimCompanion.Core.Geo;

/// <summary>Where the sun is from a point on the earth: elevation above the horizon and the
/// true azimuth it sits at.</summary>
public readonly record struct SunPosition(double ElevationDeg, double AzimuthDeg);

/// <summary>
/// A compact solar-position estimate (issue #195: the FO's "sun's getting low off the left"),
/// the NOAA low-precision algorithm — good to about a degree, which is all a remark about the
/// view needs. Pure: the sim's calendar day and zulu clock in, the sun's place out. Never used
/// for anything operational.
/// </summary>
public static class SolarPosition
{
    /// <summary>The sun's elevation and azimuth at a place and a UTC moment (NOAA's low-accuracy
    /// equations — within a degree, plenty for "is it dark yet").</summary>
    /// <param name="latitudeDeg">Aircraft latitude, degrees north.</param>
    /// <param name="longitudeDeg">Aircraft longitude, degrees east.</param>
    /// <param name="date">The calendar day (UTC).</param>
    /// <param name="utc">The time of day, UTC.</param>
    public static SunPosition At(double latitudeDeg, double longitudeDeg, DateOnly date, TimeSpan utc)
    {
        var dayOfYear = date.DayOfYear;
        var hours = utc.TotalHours;
        // Fractional year, radians.
        var gamma = 2 * Math.PI / 365.0 * (dayOfYear - 1 + (hours - 12) / 24.0);
        // Equation of time (minutes) and declination (radians).
        var eqTime = 229.18 * (0.000075 + 0.001868 * Math.Cos(gamma) - 0.032077 * Math.Sin(gamma)
            - 0.014615 * Math.Cos(2 * gamma) - 0.040849 * Math.Sin(2 * gamma));
        var decl = 0.006918 - 0.399912 * Math.Cos(gamma) + 0.070257 * Math.Sin(gamma)
            - 0.006758 * Math.Cos(2 * gamma) + 0.000907 * Math.Sin(2 * gamma)
            - 0.002697 * Math.Cos(3 * gamma) + 0.00148 * Math.Sin(3 * gamma);
        // True solar time, minutes, then the hour angle.
        var trueSolarMinutes = (hours * 60 + eqTime + 4 * longitudeDeg) % 1440;
        if (trueSolarMinutes < 0)
        {
            trueSolarMinutes += 1440;
        }

        var hourAngleDeg = trueSolarMinutes / 4 - 180;
        var lat = Rad(latitudeDeg);
        var ha = Rad(hourAngleDeg);
        var cosZenith = Math.Sin(lat) * Math.Sin(decl) + Math.Cos(lat) * Math.Cos(decl) * Math.Cos(ha);
        cosZenith = Math.Clamp(cosZenith, -1, 1);
        var zenith = Math.Acos(cosZenith);
        var elevation = 90 - Deg(zenith);

        // Azimuth, clockwise from north.
        var sinZenith = Math.Sin(zenith);
        double azimuth;
        if (Math.Abs(sinZenith) < 1e-9)
        {
            azimuth = 0;
        }
        else
        {
            var cosAz = Math.Clamp((Math.Sin(lat) * cosZenith - Math.Sin(decl)) / (Math.Cos(lat) * sinZenith), -1, 1);
            azimuth = 180 - Deg(Math.Acos(cosAz));
            if (hourAngleDeg > 0)
            {
                azimuth = 360 - azimuth;
            }
        }

        return new SunPosition(elevation, (azimuth % 360 + 360) % 360);
    }

    /// <summary>"left" / "right" / "ahead" / "behind" for a bearing relative to the track.</summary>
    public static string SideOf(double bearingDeg, double trackDeg)
    {
        var relative = ((bearingDeg - trackDeg) % 360 + 360) % 360;
        return relative switch
        {
            >= 45 and < 135 => "right",
            >= 135 and < 225 => "behind",
            >= 225 and < 315 => "left",
            _ => "ahead",
        };
    }

    private static double Rad(double deg) => deg * Math.PI / 180;

    private static double Deg(double rad) => rad * 180 / Math.PI;
}
