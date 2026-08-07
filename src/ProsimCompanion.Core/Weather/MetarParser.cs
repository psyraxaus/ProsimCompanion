using System.Globalization;
using System.Text.RegularExpressions;

namespace ProsimCompanion.Core.Weather;

/// <summary>
/// The deterministic fields extracted from a raw METAR string. All optional — a field the
/// METAR doesn't carry (or that fails to parse) is left null / <see cref="PrecipKind.None"/>.
/// </summary>
public readonly record struct MetarValues(
    int? WindDirDeg,
    int? WindSpeedKt,
    int? WindGustKt,
    int? VisibilityMeters,
    int? CeilingFt,
    PrecipKind Precip,
    double? QnhHpa,
    int? TemperatureC)
{
    public static MetarValues Empty { get; } = new(null, null, null, null, null, PrecipKind.None, null, null);
}

/// <summary>
/// A single, deterministic METAR text parser shared by every weather source (ActiveSky,
/// ProSim gateway and SayIntentions). It handles both metric (<c>9999</c>, <c>3000</c>) and
/// statute-mile (<c>P6SM</c>, <c>10SM</c>, <c>2 1/2SM</c>, <c>3/4SM</c>) visibility, the
/// lowest ceiling (BKN/OVC/VV), wind incl. VRB and gust, precipitation classification, QNH
/// (Q hPa / A inHg) and temperature. Parsing is confined to the body <b>before</b> the
/// <c>RMK</c> section so remarks (<c>RAB24</c>, <c>SLP</c>, <c>PRESRR</c>…) never produce
/// false positives (Prosim2FO rule, kept exactly). Never throws.
/// </summary>
public static class MetarParser
{
    public static MetarValues Parse(string? metar)
    {
        if (string.IsNullOrWhiteSpace(metar))
        {
            return MetarValues.Empty;
        }

        // Confine parsing to the pre-remarks body — remarks reuse tokens (RAB, SLP095, etc.).
        var body = metar;
        var rmk = body.IndexOf(" RMK ", StringComparison.OrdinalIgnoreCase);
        if (rmk >= 0)
        {
            body = body[..rmk];
        }

        body = " " + body.Trim() + " ";

        var (dir, spd, gust) = ParseWind(body);
        return new MetarValues(
            dir, spd, gust,
            ParseVisibility(body),
            ParseCeiling(body),
            ParsePrecip(body),
            ParseQnh(body),
            ParseTemp(body));
    }

    /// <summary>Convenience: parse a METAR straight into <see cref="WxFacts"/> (raw text plus
    /// every derived field), optionally carrying source-supplied ATIS/runway alongside.</summary>
    public static WxFacts ToFacts(string? metar, string? atisLetter = null, string? activeRunway = null)
    {
        var v = Parse(metar);
        return new WxFacts(
            string.IsNullOrWhiteSpace(metar) ? null : metar.Trim(),
            v.WindDirDeg, v.WindSpeedKt, v.VisibilityMeters, v.QnhHpa, v.TemperatureC,
            atisLetter, activeRunway, v.CeilingFt, v.WindGustKt, v.Precip);
    }

    private static (int? Dir, int? Spd, int? Gust) ParseWind(string body)
    {
        var m = Regex.Match(body, @"\b(\d{3}|VRB)(\d{2,3})(?:G(\d{2,3}))?KT\b");
        if (!m.Success)
        {
            return (null, null, null);
        }

        int? dir = int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var d) ? d : null; // VRB → null
        int? spd = int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var s) ? s : null;
        int? gust = m.Groups[3].Success
            && int.TryParse(m.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var g) ? g : null;
        return (dir, spd, gust);
    }

    private const double MetersPerStatuteMile = 1609.34;

    private static int? ParseVisibility(string body)
    {
        // Statute miles (North-American stations) — try these first so the metric 4-digit
        // fallback can't grab an unrelated group (e.g. the time or an RVR remnant).
        if (Regex.IsMatch(body, @"\bP6SM\b"))
        {
            return 10000;
        }

        if (Regex.IsMatch(body, @"\bM1/4SM\b"))
        {
            return 300; // "less than 1/4"
        }

        // "2 1/2SM" (whole + fraction)
        var mix = Regex.Match(body, @"\b(\d{1,2}) (\d)/(\d)SM\b");
        if (mix.Success
            && int.TryParse(mix.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var whole)
            && double.TryParse(mix.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n1)
            && double.TryParse(mix.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var d1) && d1 != 0)
        {
            return SmToMeters(whole + (n1 / d1));
        }

        // "3/4SM" (fraction only)
        var frac = Regex.Match(body, @"\b(\d)/(\d)SM\b");
        if (frac.Success
            && double.TryParse(frac.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n2)
            && double.TryParse(frac.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var d2) && d2 != 0)
        {
            return SmToMeters(n2 / d2);
        }

        // "10SM" / "1SM" (whole miles; ≥7 mi is "unlimited" in practice → 10 km cap)
        var sm = Regex.Match(body, @"\b(\d{1,2})SM\b");
        if (sm.Success && int.TryParse(sm.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var miles))
        {
            return miles >= 7 ? 10000 : SmToMeters(miles);
        }

        // Metric 4-digit (9999 → 10 km). (?!KT) keeps a calm-wind 0000KT group out.
        var metric = Regex.Match(body, @"\b(\d{4})\b(?!KT)");
        if (metric.Success
            && int.TryParse(metric.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var vm)
            && vm <= 9999)
        {
            return vm == 9999 ? 10000 : vm;
        }

        return null;
    }

    private static int SmToMeters(double miles) => (int)Math.Round(miles * MetersPerStatuteMile);

    /// <summary>Lowest broken/overcast/vertical-visibility layer, in feet AGL (hundreds → ft).
    /// FEW/SCT never constitute a ceiling. Null = no ceiling.</summary>
    private static int? ParseCeiling(string body)
    {
        int? lowest = null;
        foreach (Match m in Regex.Matches(body, @"\b(?:BKN|OVC|VV)(\d{3})\b"))
        {
            if (!int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var h))
            {
                continue;
            }

            var ft = h * 100;
            if (lowest is null || ft < lowest)
            {
                lowest = ft;
            }
        }

        return lowest;
    }

    private static PrecipKind ParsePrecip(string body)
    {
        // Priority order: TS > freezing precip > snow > rain > other frozen. Obscuration-only
        // (BR/FG/HZ) → None. Patterns kept verbatim from the empirically-tuned predecessor.
        if (Regex.IsMatch(body, @"\bTS|TS\b|\+?TSRA|VCTS"))
        {
            return PrecipKind.Thunderstorm;
        }

        if (Regex.IsMatch(body, @"\bFZ(?:RA|DZ)\b|[+-]?FZRA|[+-]?FZDZ"))
        {
            return PrecipKind.Freezing;
        }

        if (Regex.IsMatch(body, @"[+-]?(?:SH)?SN\b|\bSG\b"))
        {
            return PrecipKind.Snow;
        }

        if (Regex.IsMatch(body, @"[+-]?(?:SH)?RA\b|[+-]?DZ\b"))
        {
            return PrecipKind.Rain;
        }

        if (Regex.IsMatch(body, @"\bGR\b|\bGS\b|\bPL\b|\bIC\b"))
        {
            return PrecipKind.Other;
        }

        return PrecipKind.None;
    }

    private static double? ParseQnh(string body)
    {
        var q = Regex.Match(body, @"\bQ(\d{4})\b");
        if (q.Success && int.TryParse(q.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var hpa))
        {
            return hpa;
        }

        var a = Regex.Match(body, @"\bA(\d{4})\b");
        if (a.Success
            && double.TryParse(a.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var inHgx100))
        {
            return Math.Round(inHgx100 / 100.0 * 33.8639, 0); // inHg → hPa
        }

        return null;
    }

    private static int? ParseTemp(string body)
    {
        var t = Regex.Match(body, @"\b(M?\d{2})/(M?\d{2})\b");
        if (!t.Success)
        {
            return null;
        }

        var s = t.Groups[1].Value;
        var neg = s.StartsWith('M');
        var digits = neg ? s[1..] : s;
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? (neg ? -v : v) : null;
    }
}
