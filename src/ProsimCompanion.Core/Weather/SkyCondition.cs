using System.Globalization;
using System.Text.RegularExpressions;

namespace ProsimCompanion.Core.Weather;

/// <summary>
/// The one-glance sky picture for the Flight Status weather cards (owner request
/// 2026-09-22): "sunny, cloudy, rainy, foggy…" as a small graphic plus a caps word. Coarse by
/// design — the raw METAR stays available for the detail; this only picks the icon.
/// </summary>
public enum SkyCondition
{
    /// <summary>No observation, or nothing recognisable in it.</summary>
    Unknown,

    /// <summary>CAVOK / SKC / NSC / CLR or no cloud group at all.</summary>
    Clear,

    /// <summary>FEW or SCT only — sun behind a cloud.</summary>
    FewClouds,

    /// <summary>BKN / OVC / vertical visibility — a solid cloud.</summary>
    Overcast,

    Rain,

    Drizzle,

    Snow,

    /// <summary>FG / BR / HZ / FU obscuration without precipitation.</summary>
    Fog,

    Thunderstorm,

    /// <summary>Strong or gusting wind with nothing else going on.</summary>
    Windy,
}

/// <summary>
/// Classifies a METAR into a <see cref="SkyCondition"/> and its caps label ("FEW CLOUDS",
/// "LIGHT RAIN"). Precipitation wins over obscuration, which wins over wind, which wins over
/// cloud cover — the thing the pilot must act on first is the thing the icon shows. Parses
/// the body before RMK like <see cref="MetarParser"/> and never throws.
/// </summary>
public static class SkyConditionClassifier
{
    /// <summary>Gust or mean wind at/above this shows the wind icon when the sky is otherwise quiet.</summary>
    public const int WindyKnots = 25;

    /// <summary>Classifies <paramref name="facts"/> (its raw METAR plus the parsed wind).</summary>
    public static (SkyCondition Condition, string Label) Classify(WxFacts? facts)
    {
        if (facts?.RawMetar is null)
        {
            return (SkyCondition.Unknown, "NO DATA");
        }

        var body = Body(facts.RawMetar);

        // Present-weather groups. Intensity prefix (+/-) decides the label; the icon is the
        // same. Patterns mirror MetarParser.ParsePrecip so the two never disagree on "rain".
        if (Regex.IsMatch(body, @"\bTS|TS\b|VCTS"))
        {
            return (SkyCondition.Thunderstorm, "THUNDERSTORM");
        }

        var snow = Regex.Match(body, @"(?<![A-Z])([+-])?(?:SH|FZ)?(?:SN|SG)\b");
        if (snow.Success)
        {
            return (SkyCondition.Snow, Intensity(snow.Groups[1].Value) + "SNOW");
        }

        var rain = Regex.Match(body, @"(?<![A-Z])([+-])?(?:SH|FZ)?RA\b");
        if (rain.Success)
        {
            var freezing = body.Contains("FZRA", StringComparison.Ordinal);
            return (SkyCondition.Rain, freezing ? "FREEZING RAIN" : Intensity(rain.Groups[1].Value) + "RAIN");
        }

        var drizzle = Regex.Match(body, @"(?<![A-Z])([+-])?(?:FZ)?DZ\b");
        if (drizzle.Success)
        {
            return (SkyCondition.Drizzle, Intensity(drizzle.Groups[1].Value) + "DRIZZLE");
        }

        if (Regex.IsMatch(body, @"\b(?:GR|GS|PL|IC)\b"))
        {
            return (SkyCondition.Snow, "HAIL / ICE PELLETS");
        }

        if (Regex.IsMatch(body, @"\bFG\b|\bMIFG\b|\bBCFG\b|\bPRFG\b"))
        {
            return (SkyCondition.Fog, "FOG");
        }

        if (Regex.IsMatch(body, @"\bBR\b"))
        {
            return (SkyCondition.Fog, "MIST");
        }

        if (Regex.IsMatch(body, @"\bHZ\b|\bFU\b|\bDU\b|\bSA\b"))
        {
            return (SkyCondition.Fog, "HAZE");
        }

        var strongest = Math.Max(facts.WindGustKt ?? 0, facts.WindSpeedKt ?? 0);
        if (strongest >= WindyKnots)
        {
            return (SkyCondition.Windy, facts.WindGustKt is not null ? "GUSTY WIND" : "STRONG WIND");
        }

        if (Regex.IsMatch(body, @"\bOVC\d{3}\b|\bVV\d{3}\b"))
        {
            return (SkyCondition.Overcast, "OVERCAST");
        }

        if (Regex.IsMatch(body, @"\bBKN\d{3}\b"))
        {
            return (SkyCondition.Overcast, "BROKEN CLOUD");
        }

        if (Regex.IsMatch(body, @"\bSCT\d{3}\b"))
        {
            return (SkyCondition.FewClouds, "SCATTERED CLOUD");
        }

        if (Regex.IsMatch(body, @"\bFEW\d{3}\b"))
        {
            return (SkyCondition.FewClouds, "FEW CLOUDS");
        }

        return (SkyCondition.Clear, "CLEAR");
    }

    /// <summary>The observation time ("10:20Z") from the METAR's <c>ddhhmmZ</c> group, or
    /// null — the freshness stamp on the weather cards.</summary>
    public static string? ObservationTime(string? metar)
    {
        if (string.IsNullOrWhiteSpace(metar))
        {
            return null;
        }

        var m = Regex.Match(Body(metar), @"\b\d{2}(\d{2})(\d{2})Z\b");
        if (!m.Success)
        {
            return null;
        }

        return int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var hh) && hh < 24
            && int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var mm) && mm < 60
            ? $"{hh:00}:{mm:00}Z"
            : null;
    }

    private static string Body(string metar)
    {
        var body = metar;
        var rmk = body.IndexOf(" RMK ", StringComparison.OrdinalIgnoreCase);
        if (rmk >= 0)
        {
            body = body[..rmk];
        }

        return " " + body.Trim().ToUpperInvariant() + " ";
    }

    private static string Intensity(string prefix) => prefix switch
    {
        "+" => "HEAVY ",
        "-" => "LIGHT ",
        _ => "",
    };
}
