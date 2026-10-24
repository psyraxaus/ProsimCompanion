namespace ProsimCompanion.Core.Aircraft.Ofp;

/// <summary>One NOTAM as SimBrief's JSON carries it (issue #189): the departure and destination
/// lists (<c>origin.notam</c>, <c>destination.notam</c>) and the FIR records filtered to those
/// two stations. Times UTC; a null expiry is PERM.</summary>
public sealed record OfpNotam(
    string Id,
    string Icao,
    string QCode,
    string Text,
    DateTimeOffset? EffectiveUtc,
    DateTimeOffset? ExpiresUtc,
    bool ExpiryEstimated);

/// <summary>The keyword classes a NOTAM highlight belongs to — what a crew reads first.</summary>
public enum NotamHighlightClass
{
    RunwayClosed,
    TaxiwayClosed,
    IlsUnserviceable,
    LightingUnserviceable,
    Papi,
    DisplacedThreshold,
}

/// <summary>A NOTAM worth a line on the OFP page and in the threats clause.</summary>
public sealed record NotamHighlight(OfpNotam Notam, NotamHighlightClass Class);

/// <summary>
/// Picks the operationally loud NOTAMs out of the list (issue #189): Q-code first (the ICAO
/// subject/status letters are unambiguous), the E-line keywords as the fallback for a NOTAM
/// with no Q-code. Pure; everything else (obstacles, AIP references, procedures, DGS) stays off
/// the highlights.
/// </summary>
public static class NotamHighlighter
{
    /// <summary>The class, or null when the NOTAM is not a highlight.</summary>
    public static NotamHighlightClass? Classify(OfpNotam notam)
    {
        ArgumentNullException.ThrowIfNull(notam);
        var q = (notam.QCode ?? "").Trim().ToUpperInvariant();
        var text = (notam.Text ?? "").ToUpperInvariant();

        if (q.Length >= 5)
        {
            var subject = q.Substring(1, 2);
            var status = q.Substring(3, 2);
            var closed = status is "LC" or "LT" && text.Contains("CLSD", StringComparison.Ordinal);
            var unserviceable = status is "AS" or "XX" && (text.Contains("U/S", StringComparison.Ordinal) || text.Contains("UNSERVICEABLE", StringComparison.Ordinal) || status == "AS");
            switch (subject)
            {
                case "MR" when status == "LC" || closed:
                    return NotamHighlightClass.RunwayClosed;
                case "MX" when status == "LC" || closed:
                    return NotamHighlightClass.TaxiwayClosed;
                case "IC" or "IL" or "IG" or "ID" or "II" or "IM" or "IO" when unserviceable || status is "LC" or "LT":
                    return NotamHighlightClass.IlsUnserviceable;
                case "LP" or "LV":
                    return NotamHighlightClass.Papi;
                case "MT" or "MD":
                    return NotamHighlightClass.DisplacedThreshold;
                case "LA" or "LB" or "LC" or "LD" or "LE" or "LF" or "LH" or "LI" or "LJ" or "LK" or "LL" or "LM" or "LR" or "LS" or "LT" or "LU" or "LW" or "LX" or "LY" or "LZ" when unserviceable:
                    return NotamHighlightClass.LightingUnserviceable;
            }
        }

        // Keyword fallback for a NOTAM without a usable Q-code.
        if (string.IsNullOrWhiteSpace(q) || q.Length < 5)
        {
            if (text.Contains("RWY", StringComparison.Ordinal) && text.Contains("CLSD", StringComparison.Ordinal))
            {
                return NotamHighlightClass.RunwayClosed;
            }

            if (text.Contains("TWY", StringComparison.Ordinal) && text.Contains("CLSD", StringComparison.Ordinal))
            {
                return NotamHighlightClass.TaxiwayClosed;
            }

            if ((text.Contains("ILS", StringComparison.Ordinal) || text.Contains("LOC", StringComparison.Ordinal) || text.Contains("GP ", StringComparison.Ordinal) || text.Contains("GLIDE", StringComparison.Ordinal))
                && (text.Contains("U/S", StringComparison.Ordinal) || text.Contains("UNSERVICEABLE", StringComparison.Ordinal) || text.Contains("NOT AVBL", StringComparison.Ordinal)))
            {
                return NotamHighlightClass.IlsUnserviceable;
            }

            if (text.Contains("PAPI", StringComparison.Ordinal))
            {
                return NotamHighlightClass.Papi;
            }

            if (text.Contains("THR", StringComparison.Ordinal) && text.Contains("DISPLACED", StringComparison.Ordinal))
            {
                return NotamHighlightClass.DisplacedThreshold;
            }

            if ((text.Contains("LGT", StringComparison.Ordinal) || text.Contains("LIGHT", StringComparison.Ordinal))
                && (text.Contains("U/S", StringComparison.Ordinal) || text.Contains("UNSERVICEABLE", StringComparison.Ordinal)))
            {
                return NotamHighlightClass.LightingUnserviceable;
            }
        }

        return null;
    }

    /// <summary>The highlights for one station, in class order (runways first), each NOTAM once.</summary>
    public static IReadOnlyList<NotamHighlight> Highlights(IReadOnlyList<OfpNotam> notams, string? icao = null)
    {
        ArgumentNullException.ThrowIfNull(notams);
        return notams
            .Where(n => icao is null || n.Icao.Equals(icao, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(n => n.Id, StringComparer.OrdinalIgnoreCase)
            .Select(n => (Notam: n, Class: Classify(n)))
            .Where(x => x.Class is not null)
            .OrderBy(x => x.Class)
            .Select(x => new NotamHighlight(x.Notam, x.Class!.Value))
            .ToList();
    }

    /// <summary>The E-line only, one line, trimmed to <paramref name="maxLength"/> for a card row.</summary>
    public static string ShortText(OfpNotam notam, int maxLength = 140)
    {
        ArgumentNullException.ThrowIfNull(notam);
        var text = string.Join(' ', (notam.Text ?? "").Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= maxLength ? text : text[..(maxLength - 1)].TrimEnd() + "…";
    }
}
