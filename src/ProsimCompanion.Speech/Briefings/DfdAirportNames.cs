using System.Collections.Concurrent;
using System.Text;
using ProsimCompanion.Core.Airports;

namespace ProsimCompanion.Speech.Briefings;

/// <summary>
/// <see cref="IAirportNames"/> over the Navigraph DFD's <c>airport_name</c> column (issue
/// #70). Curated overrides win over the DFD because the database stores the formal name
/// ("LONDON HEATHROW") where crews say the short one ("Heathrow") — and they also work with
/// no DFD configured at all. DFD hits are title-cased for TTS and cached in memory; misses
/// are deliberately NOT cached, so a DFD configured mid-session starts answering without a
/// restart (lookups are rare — briefings and company messages — so the re-query is cheap).
/// </summary>
public sealed class DfdAirportNames : IAirportNames
{
    // The short forms crews actually say. Deliberately a simple static list, not config —
    // an unlisted airport just falls through to the DFD's formal name.
    private static readonly Dictionary<string, string> Overrides = new(StringComparer.OrdinalIgnoreCase)
    {
        ["EGLL"] = "Heathrow",
        ["EHAM"] = "Schiphol",
        ["KJFK"] = "Kennedy",
        ["EDDF"] = "Frankfurt",
        ["LFPG"] = "Charles de Gaulle",
        ["KLAX"] = "Los Angeles",
        ["EGKK"] = "Gatwick",
        ["EGSS"] = "Stansted",
        ["LSZH"] = "Zurich",
        ["LOWW"] = "Vienna",
        ["EDDM"] = "Munich",
        ["LEMD"] = "Madrid",
        ["LIRF"] = "Rome Fiumicino",
        ["KSFO"] = "San Francisco",
        ["YSSY"] = "Sydney",
        ["YMML"] = "Melbourne",
        ["YBBN"] = "Brisbane",
        ["YPPH"] = "Perth",
        ["NZAA"] = "Auckland",
        ["WSSS"] = "Changi",
        ["VHHH"] = "Hong Kong",
        ["RJTT"] = "Haneda",
        ["OMDB"] = "Dubai",
    };

    private readonly DfdNavDataProvider _navData;
    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);

    public DfdAirportNames(DfdNavDataProvider navData)
    {
        ArgumentNullException.ThrowIfNull(navData);
        _navData = navData;
    }

    /// <inheritdoc />
    public string? SpokenName(string? icao)
    {
        if (string.IsNullOrWhiteSpace(icao))
        {
            return null;
        }

        var key = icao.Trim().ToUpperInvariant();
        if (Overrides.TryGetValue(key, out var curated))
        {
            return curated;
        }

        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var raw = _navData.AirportName(key); // already null on any DFD/query failure
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var name = TitleCase(raw);
        _cache[key] = name;
        return name;
    }

    /// <summary>"LONDON HEATHROW" → "London Heathrow". The DFD stores names ALL-CAPS, which
    /// some TTS engines spell out letter by letter; simple word-boundary title-casing (also
    /// after hyphens/slashes) is enough for speech — no locale-aware casing needed.</summary>
    public static string TitleCase(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var sb = new StringBuilder(name.Length);
        var startOfWord = true;
        foreach (var c in name)
        {
            if (char.IsLetter(c))
            {
                sb.Append(startOfWord ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c));
                startOfWord = false;
            }
            else
            {
                sb.Append(c);
                startOfWord = true;
            }
        }

        return sb.ToString().Trim();
    }
}
