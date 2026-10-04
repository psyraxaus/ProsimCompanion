using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace ProsimCompanion.Core.Airports.Parking;

/// <summary>
/// Merges every registered <see cref="IAirportParkingSource"/> into one catalogue per airport
/// and caches it. Sources are asked in parallel; records with the same
/// <see cref="ParkingIdentity"/> merge (GSX profile first, so its names and radii win ties;
/// facility data fills the rest). A source that throws is logged and skipped — one missing
/// profile folder must never hide the scenery's own parking list.
/// </summary>
public sealed class AirportParkingCatalog : IAirportParkingCatalog
{
    private readonly IReadOnlyList<IAirportParkingSource> _sources;
    private readonly ILogger<AirportParkingCatalog> _logger;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, Lazy<Task<AirportParkings?>>> _cache = new(StringComparer.OrdinalIgnoreCase);

    public AirportParkingCatalog(IEnumerable<IAirportParkingSource> sources, ILogger<AirportParkingCatalog> logger, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(logger);
        _sources = [.. sources];
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<AirportParkings?> GetAsync(string icao, bool refresh = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(icao))
        {
            return null;
        }

        var key = icao.Trim().ToUpperInvariant();
        if (refresh)
        {
            _cache.TryRemove(key, out _);
        }

        var lazy = _cache.GetOrAdd(key, k => new Lazy<Task<AirportParkings?>>(() => LoadAsync(k)));
        try
        {
            var result = await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (result is null)
            {
                // Nothing known yet (sim not connected, no profile): do not pin the miss — the
                // next call asks again, cheaply, and succeeds once a source comes online.
                _cache.TryRemove(key, out _);
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
    }

    private async Task<AirportParkings?> LoadAsync(string icao)
    {
        var results = new List<(IAirportParkingSource Source, AirportParkingSourceResult Result)>();
        var tasks = _sources.Select(async source =>
        {
            try
            {
                var result = await source.LoadAsync(icao).ConfigureAwait(false);
                if (result is not null)
                {
                    lock (results)
                    {
                        results.Add((source, result));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Parking source {Source} failed for {Icao}", source.Name, icao);
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);

        if (results.Count == 0)
        {
            return null;
        }

        // Deterministic merge order: source precedence, then registration order.
        var registration = _sources.ToList();
        var ordered = results.OrderBy(r => r.Source.Order).ThenBy(r => registration.IndexOf(r.Source)).ToList();
        var merged = new Dictionary<ParkingIdentity, AirportParking>();
        var sources = ParkingDataSources.None;
        var notes = new List<string>();
        foreach (var (source, result) in ordered)
        {
            notes.Add($"{source.Name}: {result.Note}");
            foreach (var parking in result.Parkings)
            {
                sources |= parking.Sources;
                merged[parking.Identity] = merged.TryGetValue(parking.Identity, out var existing)
                    ? existing.MergeWith(parking)
                    : parking;
            }
        }

        var list = merged.Values
            .OrderBy(p => p.Identity.Name)
            .ThenBy(p => p.Identity.Number)
            .ThenBy(p => p.Identity.Suffix, StringComparer.Ordinal)
            .ToList();
        _logger.LogInformation("Parking catalogue for {Icao}: {Count} stands from {Notes}", icao, list.Count, string.Join("; ", notes));
        LogPoseAgreement(icao, ordered.Select(r => r.Result.Parkings).ToList());
        return new AirportParkings(icao, list, sources, notes, _clock.GetUtcNow());
    }

    /// <summary>
    /// Self-check for the facility reader's BIAS_X/BIAS_Z sign convention (unverified until the
    /// first live flight): where the GSX ini and the scenery both position the same stand, the
    /// two should sit within a few metres. A median of hundreds of metres means an axis is
    /// flipped — the log line is the evidence to fix it from.
    /// </summary>
    private void LogPoseAgreement(string icao, IReadOnlyList<IReadOnlyList<AirportParking>> perSource)
    {
        if (perSource.Count < 2)
        {
            return;
        }

        var distancesM = new List<double>();
        var first = perSource[0].Where(p => p.Pose is not null).ToDictionary(p => p.Identity, p => p.Pose!.Value);
        foreach (var other in perSource.Skip(1))
        {
            foreach (var parking in other)
            {
                if (parking.Pose is { } pose && first.TryGetValue(parking.Identity, out var reference))
                {
                    distancesM.Add(Flight.GreatCircle.DistanceNm(reference.Position, pose.Position) * 1852.0);
                }
            }
        }

        if (distancesM.Count == 0)
        {
            return;
        }

        distancesM.Sort();
        var median = distancesM[distancesM.Count / 2];
        _logger.LogInformation(
            "Parking positions at {Icao}: {Count} stands known to two sources, median offset {Median:F0} m, max {Max:F0} m{Verdict}",
            icao, distancesM.Count, median, distancesM[^1],
            median > 60 ? " — LARGE: check the facility BIAS_X/BIAS_Z axis convention" : "");
    }
}
