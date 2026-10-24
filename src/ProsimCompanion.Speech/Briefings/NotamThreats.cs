using System.Globalization;
using System.Text.RegularExpressions;
using ProsimCompanion.Core.Aircraft.Ofp;

namespace ProsimCompanion.Speech.Briefings;

/// <summary>
/// The departure NOTAM highlights as threat lines for the takeoff briefing (issues #180 + #189,
/// wired at the November 2026 merge): one line per highlighted NOTAM, the runway or taxiway
/// designators lifted from the E line so the FO says "runway 12 closed by NOTAM" rather than
/// reading the NOTAM. Pure; the classifier is the Core <see cref="NotamHighlighter"/>.
/// </summary>
public static partial class NotamThreats
{
    /// <summary>At most this many NOTAM lines in the threats clause — the rest stay on the OFP card.</summary>
    public const int MaxLines = 4;

    public static IReadOnlyList<string> Lines(IReadOnlyList<OfpNotam> notams, string? departureIcao)
    {
        ArgumentNullException.ThrowIfNull(notams);
        if (string.IsNullOrWhiteSpace(departureIcao))
        {
            return [];
        }

        var lines = new List<string>();
        foreach (var h in NotamHighlighter.Highlights(notams, departureIcao))
        {
            var text = (h.Notam.Text ?? "").ToUpperInvariant();
            var runways = Designators(RunwayPattern(), text);
            var taxiways = Designators(TaxiwayPattern(), text);
            var line = h.Class switch
            {
                NotamHighlightClass.RunwayClosed => runways.Length > 0 ? $"runway {runways} closed by NOTAM" : "a runway closed by NOTAM",
                NotamHighlightClass.TaxiwayClosed => taxiways.Length > 0 ? $"taxiway {taxiways} closed" : "a taxiway closed",
                NotamHighlightClass.IlsUnserviceable => runways.Length > 0 ? $"ILS runway {runways} unserviceable" : "an ILS unserviceable",
                NotamHighlightClass.LightingUnserviceable => runways.Length > 0 ? $"runway {runways} lighting unserviceable" : "airfield lighting unserviceable",
                NotamHighlightClass.Papi => runways.Length > 0 ? $"PAPI runway {runways} unserviceable" : "a PAPI unserviceable",
                NotamHighlightClass.DisplacedThreshold => runways.Length > 0 ? $"displaced threshold runway {runways}" : "a displaced threshold",
                _ => null,
            };
            if (line is not null && !lines.Contains(line, StringComparer.Ordinal))
            {
                lines.Add(line);
            }

            if (lines.Count == MaxLines)
            {
                break;
            }
        }

        return lines;
    }

    /// <summary>The figures the lines speak (runway numbers), for the allowed-number set.</summary>
    public static IEnumerable<double> Numbers(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        foreach (var line in lines)
        {
            foreach (Match m in NumberPattern().Matches(line))
            {
                yield return double.Parse(m.Value, CultureInfo.InvariantCulture);
            }
        }
    }

    /// <summary>"RWY 12", "RWY 04R/22L", "RWY 09 AND 27" → "12", "04R and 22L", "09 and 27".</summary>
    private static string Designators(Regex pattern, string text)
    {
        var found = pattern.Matches(text).SelectMany(m => m.Groups["id"].Captures.Select(c => c.Value)).Distinct(StringComparer.Ordinal).ToList();
        return found.Count switch
        {
            0 => "",
            1 => found[0],
            _ => string.Join(", ", found.Take(found.Count - 1)) + " and " + found[^1],
        };
    }

    [GeneratedRegex(@"\bRWY\s*(?<id>\d{2}[LRC]?)(?:\s*(?:/|AND)\s*(?<id>\d{2}[LRC]?))*")]
    private static partial Regex RunwayPattern();

    [GeneratedRegex(@"\bTWY\s*(?<id>[A-Z]{1,2}\d{0,2})(?:\s*(?:/|AND)\s*(?<id>[A-Z]{1,2}\d{0,2}))*")]
    private static partial Regex TaxiwayPattern();

    [GeneratedRegex(@"\d+")]
    private static partial Regex NumberPattern();
}
