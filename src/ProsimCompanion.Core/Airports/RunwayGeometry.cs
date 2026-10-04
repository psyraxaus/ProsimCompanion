using System.Globalization;
using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Aircraft.Gateway;
using ProsimCompanion.Core.Flight;

namespace ProsimCompanion.Core.Airports;

/// <summary>Where a runway starts and which way it points — what the pushback advisor needs
/// to know which push direction faces the departure runway.</summary>
/// <param name="Icao">Airport.</param>
/// <param name="Ident">Runway as asked ("22L"), normalised upper-case without the RW prefix.</param>
/// <param name="Threshold">Threshold (or the nearest runway point the source has).</param>
/// <param name="TrueHeadingDeg">True heading of the runway, when the source knows it.</param>
/// <param name="LengthFt">Length, when known.</param>
/// <param name="Source">Which tier answered ("gateway", "dfd").</param>
public sealed record RunwayGeometry(string Icao, string Ident, GeoPoint Threshold, double? TrueHeadingDeg, int? LengthFt, string Source);

/// <summary>
/// The departure airport + runway as currently planned, for the pushback advisor: the FMS
/// plan when the Speech pillar is composed in (its briefing procedure source reads the MCDU
/// flight plan, the configured datarefs and SayIntentions' flight.json), else the OFP's
/// planned runway. Implementations never throw; "unknown" is null.
/// </summary>
public interface IDepartureRunwaySource
{
    /// <summary>(airport ICAO, runway ident) of the departure, or null when unknown.</summary>
    (string Airport, string Runway)? Current();
}

/// <summary>One place runway geometry can come from (same seam shape as
/// <see cref="IAirportCoordinateSource"/>): never throw, "unknown" is null.</summary>
public interface IRunwayGeometrySource
{
    string Name { get; }

    /// <summary>Lower runs first.</summary>
    int Order { get; }

    Task<RunwayGeometry?> FindAsync(string icao, string runway, CancellationToken cancellationToken = default);
}

/// <summary>Resolves a runway through the registered tiers in order; the first answer wins.</summary>
public sealed class RunwayLocator
{
    private readonly IReadOnlyList<IRunwayGeometrySource> _sources;
    private readonly ILogger<RunwayLocator> _logger;

    public RunwayLocator(IEnumerable<IRunwayGeometrySource> sources, ILogger<RunwayLocator> logger)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(logger);
        _sources = [.. sources.OrderBy(s => s.Order)];
        _logger = logger;
    }

    /// <summary>"RW22L" / "22l" / "22L" → "22L"; "9R" → "09R"; null for blank.</summary>
    public static string? NormalizeIdent(string? runway)
    {
        if (string.IsNullOrWhiteSpace(runway))
        {
            return null;
        }

        var id = runway.Trim().ToUpperInvariant();
        if (id.StartsWith("RW", StringComparison.Ordinal))
        {
            id = id[2..];
        }

        if (id.Length >= 1 && char.IsDigit(id[0]) && (id.Length == 1 || !char.IsDigit(id[1])))
        {
            id = "0" + id;
        }

        return id.Length == 0 ? null : id;
    }

    public async Task<RunwayGeometry?> FindAsync(string? icao, string? runway, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(icao) || NormalizeIdent(runway) is not { } ident)
        {
            return null;
        }

        var id = icao.Trim().ToUpperInvariant();
        foreach (var source in _sources)
        {
            try
            {
                if (await source.FindAsync(id, ident, cancellationToken).ConfigureAwait(false) is { } geometry)
                {
                    return geometry;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Runway source {Source} failed for {Icao} {Runway}", source.Name, id, ident);
            }
        }

        return null;
    }
}

/// <summary>
/// Runway geometry from the ProSim EFB gateway's runway list. The coordinate shape of the
/// DTO is unverified live (see <see cref="GatewayAirportCoordinates"/>): the first usable
/// point of the matching runway is taken as its threshold, and <c>hdgDegT</c> as the true
/// heading. Good enough to tell "toward the runway" from "away from it".
/// </summary>
public sealed class GatewayRunwayGeometry : IRunwayGeometrySource
{
    private readonly IProsimGateway _gateway;
    private readonly ILogger<GatewayRunwayGeometry> _logger;

    public GatewayRunwayGeometry(IProsimGateway gateway, ILogger<GatewayRunwayGeometry> logger)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(logger);
        _gateway = gateway;
        _logger = logger;
    }

    public string Name => "gateway";

    public int Order => 10;

    public async Task<RunwayGeometry?> FindAsync(string icao, string runway, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await _gateway.IsReachableAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            var runways = await _gateway.GetRunwaysAsync(icao, includeIntersections: false, cancellationToken).ConfigureAwait(false);
            return FromRunways(icao, runway, runways);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Gateway runway fetch failed for {Icao} {Runway}", icao, runway);
            return null;
        }
    }

    /// <summary>Pure: pick the runway whose id normalises to <paramref name="runway"/>. Public for tests.</summary>
    public static RunwayGeometry? FromRunways(string icao, string runway, IReadOnlyList<RunwayResponse>? runways)
    {
        var wanted = RunwayLocator.NormalizeIdent(runway);
        if (wanted is null || runways is null)
        {
            return null;
        }

        var match = runways.FirstOrDefault(r => RunwayLocator.NormalizeIdent(r.RunwayId) == wanted);
        if (match is null)
        {
            return null;
        }

        GeoPoint? threshold = null;
        if (match.Lat is { Longitude: 0 } latOnly && match.Lng is { Latitude: 0 } lonOnly)
        {
            threshold = GeoPoint.FromRaw(latOnly.Latitude, lonOnly.Longitude);
        }
        else if (match.Lat is { } lat)
        {
            threshold = GeoPoint.FromRaw(lat.Latitude, lat.Longitude);
        }

        if (threshold is null && match.Lng is { } lng)
        {
            threshold = GeoPoint.FromRaw(lng.Latitude, lng.Longitude);
        }

        if (threshold is null)
        {
            return null;
        }

        double? heading = double.TryParse(match.HdgDegT, NumberStyles.Float, CultureInfo.InvariantCulture, out var h) && h is >= 0 and <= 360
            ? h
            : null;
        return new RunwayGeometry(icao, wanted, threshold.Value, heading, match.LengthFt, "gateway");
    }
}
