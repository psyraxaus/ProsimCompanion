using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Flight;

namespace ProsimCompanion.Core.Airports;

/// <summary>Where an airport is: its reference point, its elevation when the source knows it,
/// and which source answered (shown on the status surfaces and written to the session log,
/// so a wrong distance can be traced to its origin).</summary>
public sealed record AirportLocation(string Icao, GeoPoint Position, double? ElevationFt, string Source);

/// <summary>
/// One place airport coordinates can come from (issue #145). A Core seam like
/// <see cref="IAirportNames"/>: the progress store needs coordinates for the OFP's origin,
/// destination and alternate without knowing whether the ProSim gateway or the Navigraph DFD
/// supplied them. Implementations never throw — "unknown" is null.
/// </summary>
public interface IAirportCoordinateSource
{
    /// <summary>Short name for logs and the session event ("gateway", "dfd").</summary>
    string Name { get; }

    /// <summary>Lower runs first. Gateway 10, DFD 20.</summary>
    int Order { get; }

    /// <summary>The airport's location, or null when this source does not know it (blank
    /// ICAO, source absent, airport missing). Only cancellation may throw.</summary>
    Task<AirportLocation?> FindAsync(string icao, CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolves an ICAO through the registered <see cref="IAirportCoordinateSource"/>s in order;
/// the first answer wins. No sources, or none that know the airport, is null — the caller
/// degrades (the progress line falls back to its time-based values).
/// </summary>
public sealed class AirportLocator
{
    private readonly IReadOnlyList<IAirportCoordinateSource> _sources;
    private readonly ILogger<AirportLocator> _logger;

    public AirportLocator(IEnumerable<IAirportCoordinateSource> sources, ILogger<AirportLocator> logger)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(logger);

        _sources = [.. sources.OrderBy(source => source.Order)];
        _logger = logger;
    }

    public async Task<AirportLocation?> FindAsync(string? icao, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(icao))
        {
            return null;
        }

        var id = icao.Trim().ToUpperInvariant();
        foreach (var source in _sources)
        {
            try
            {
                if (await source.FindAsync(id, cancellationToken).ConfigureAwait(false) is { } location)
                {
                    return location;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The contract says sources do not throw; one that does must not take the
                // other tiers down with it.
                _logger.LogWarning(ex, "Airport coordinate source {Source} failed for {Icao}", source.Name, id);
            }
        }

        return null;
    }
}
