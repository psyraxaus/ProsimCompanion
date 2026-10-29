using System.Collections.Concurrent;
using ProsimCompanion.Core.Speech;

namespace ProsimCompanion.Speech.Briefings;

/// <summary>
/// <see cref="IFixNames"/> over the Navigraph DFD's navaid and waypoint name columns (owner
/// request 2026-10-29), the tier behind the OFP navlog: VOR/DME first, then NDB, the airport's
/// terminal waypoints, then enroute waypoints. Identifiers are shared world-wide ("LAM" is
/// Lambourne, Lamezia and Lamerd; the enroute waypoint "BPK" is an Australian weather radar),
/// so <see cref="DfdNavDataProvider.FixName"/> keeps only the rows in the airport's ICAO region
/// and, among those, the nearest to the airport's reference point — with no airport hint an
/// ambiguous ident yields null rather than a wrong name. Hits are cached per (ident, airport);
/// misses are not, so a DFD configured mid-session starts answering without a restart.
/// </summary>
public sealed class DfdFixNames : IFixNames
{
    private readonly DfdNavDataProvider _navData;
    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);

    public DfdFixNames(DfdNavDataProvider navData)
    {
        ArgumentNullException.ThrowIfNull(navData);
        _navData = navData;
    }

    /// <inheritdoc />
    public string? Name(string ident, string? nearAirportIcao)
    {
        if (string.IsNullOrWhiteSpace(ident))
        {
            return null;
        }

        var cacheName = ident.Trim().ToUpperInvariant() + "|" + (nearAirportIcao?.Trim().ToUpperInvariant() ?? "");
        if (_cache.TryGetValue(cacheName, out var cached))
        {
            return cached;
        }

        var name = _navData.FixName(ident, nearAirportIcao); // null on any DFD/query failure
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        _cache[cacheName] = name;
        return name;
    }
}

/// <summary>The fix-name tiers in order: the OFP navlog (this flight's own fixes, unambiguous),
/// then the DFD. The first non-null answer wins; every tier is optional.</summary>
public sealed class FixNameChain : IFixNames
{
    private readonly IReadOnlyList<IFixNames> _tiers;

    public FixNameChain(IEnumerable<IFixNames> tiers)
    {
        ArgumentNullException.ThrowIfNull(tiers);
        _tiers = tiers.ToList();
    }

    /// <inheritdoc />
    public string? Name(string ident, string? nearAirportIcao)
    {
        foreach (var tier in _tiers)
        {
            try
            {
                if (tier.Name(ident, nearAirportIcao) is { Length: > 0 } name)
                {
                    return name;
                }
            }
            catch (Exception)
            {
                // A tier that throws is a tier with no answer — the next one is asked.
            }
        }

        return null;
    }
}
