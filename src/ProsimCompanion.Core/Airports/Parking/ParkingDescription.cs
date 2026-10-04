using System.Globalization;

namespace ProsimCompanion.Core.Airports.Parking;

/// <summary>Pure one-line descriptions of a stand for the OFP page, the status line and the FO.</summary>
public static class ParkingDescription
{
    /// <summary>"GSX knows it as Gate 40 (Apron 1W (Gates W34-W48)) · jetway · max span 65 m ·
    /// Safedock · heavy gate" — only the facts the sources carry; the first clause always.</summary>
    public static string Describe(ParkingMatch match)
    {
        ArgumentNullException.ThrowIfNull(match);
        var p = match.Parking;
        var parts = new List<string>();

        var name = p.GsxGateName ?? p.Identity.DefaultDisplayName;
        var group = GroupOf(p.GsxUiName);
        parts.Add(p.Sources.HasFlag(ParkingDataSources.GsxPy)
            ? $"GSX knows it as {name}{(group is null ? "" : $" ({group})")}"
            : $"scenery stand {name}");

        if (match.Confidence == ParkingMatchConfidence.Likely)
        {
            parts.Add("best match");
        }
        else if (match.Confidence == ParkingMatchConfidence.Ambiguous)
        {
            parts.Add($"also {string.Join(", ", match.Alternatives.Select(a => a.DisplayName))}");
        }

        if (p.HasJetway is { } jetway)
        {
            parts.Add(jetway ? "jetway" : "no jetway");
        }

        if (p.MaxWingspanM is { } span)
        {
            parts.Add($"max span {span.ToString("0", CultureInfo.InvariantCulture)} m");
        }
        else if (p.RadiusM is { } radius)
        {
            parts.Add($"radius {radius.ToString("0", CultureInfo.InvariantCulture)} m");
        }

        if (!string.IsNullOrWhiteSpace(p.ParkingSystem))
        {
            parts.Add(FriendlyParkingSystem(p.ParkingSystem));
        }

        if (TypeWord(p.TypeCode) is { } type)
        {
            parts.Add(type);
        }

        if (p.AirlineCodes.Count > 0)
        {
            parts.Add($"airlines {string.Join("/", p.AirlineCodes.Take(4))}");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>The facility group of a GSX full name — "Apron 1W (Gates W34-W48)" of
    /// "Apron 1W (Gates W34-W48) | Gate 40"; null without a bar.</summary>
    public static string? GroupOf(string? uiName)
    {
        if (string.IsNullOrWhiteSpace(uiName))
        {
            return null;
        }

        var bar = uiName.LastIndexOf('|');
        if (bar <= 0)
        {
            return null;
        }

        var group = uiName[..bar].Trim();
        return group.Length == 0 ? null : group;
    }

    /// <summary>SDK parking type codes in plain words (GSX ini <c>type</c>).</summary>
    public static string? TypeWord(int? code) => code switch
    {
        1 or 2 or 3 or 4 or 14 => "GA ramp",
        5 => "cargo ramp",
        6 => "military cargo",
        7 => "military combat",
        8 => "small gate",
        9 => "medium gate",
        10 => "heavy gate",
        15 => "extra-large gate",
        11 => "dock",
        12 => "fuel",
        13 => "vehicle",
        _ => null,
    };

    private static string FriendlyParkingSystem(string system)
    {
        var s = system.Trim();
        if (s.StartsWith("SafeDock", StringComparison.OrdinalIgnoreCase) || s.Contains("VDGS", StringComparison.OrdinalIgnoreCase))
        {
            return "VDGS";
        }

        return s.Equals("Marshaller", StringComparison.OrdinalIgnoreCase) ? "marshaller" : s;
    }
}
