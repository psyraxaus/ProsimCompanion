using System.Globalization;
using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Aircraft.Gateway;
using ProsimCompanion.Core.Flight;

namespace ProsimCompanion.Core.Airports;

/// <summary>
/// Airport coordinates from the ProSim EFB gateway's runway list
/// (<c>GET /efb/airport/{icao}/runways</c>) — the first tier (issue #145). The gateway has no
/// airport reference point, so the location is the centre of the runway coordinates it does
/// send, and the elevation is the mean runway elevation. That is within a mile or two of the
/// published reference point: plenty for a great-circle distance.
/// <para>
/// The wire shape of the runway coordinates is unverified live: the DTO (carried from the
/// predecessor) types BOTH <c>lat</c> and <c>lng</c> as a latitude/longitude pair, which
/// reads like two runway points. Every pair that passes <see cref="GeoPoint.FromRaw"/> is
/// used and the count is logged, so the first flight settles what the gateway really sends.
/// No usable point = null, and the DFD tier answers instead.
/// </para>
/// </summary>
public sealed class GatewayAirportCoordinates : IAirportCoordinateSource
{
    /// <summary>Furthest a runway point may sit from the computed centre, nm.</summary>
    private const double MaxSpreadNm = 15;

    private readonly IProsimGateway _gateway;
    private readonly ILogger<GatewayAirportCoordinates> _logger;

    public GatewayAirportCoordinates(IProsimGateway gateway, ILogger<GatewayAirportCoordinates> logger)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(logger);

        _gateway = gateway;
        _logger = logger;
    }

    public string Name => "gateway";

    public int Order => 10;

    public async Task<AirportLocation?> FindAsync(string icao, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(icao))
        {
            return null;
        }

        var id = icao.Trim().ToUpperInvariant();
        try
        {
            // The cheap probe first: the runway fetch retries and logs warnings, which a
            // ProSim that is simply not running should not earn every time an OFP loads.
            if (!await _gateway.IsReachableAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            var runways = await _gateway.GetRunwaysAsync(id, includeIntersections: false, cancellationToken).ConfigureAwait(false);
            var location = FromRunways(id, runways);
            _logger.LogInformation(
                "Gateway airport {Icao}: {Runways} runway(s), {Points} coordinate point(s) -> {Result}",
                id, runways?.Count ?? 0, Points(runways).Count,
                location is null
                    ? "no usable position"
                    : string.Create(CultureInfo.InvariantCulture,
                        $"{location.Position.LatitudeDeg:F4},{location.Position.LongitudeDeg:F4} elev {location.ElevationFt?.ToString("F0", CultureInfo.InvariantCulture) ?? "?"} ft"));
            return location;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Gateway runway fetch failed for {Icao}", id);
            return null;
        }
    }

    /// <summary>Pure reduction of a runway list to one location. Public for tests.</summary>
    public static AirportLocation? FromRunways(string icao, IReadOnlyList<RunwayResponse>? runways)
    {
        var points = Points(runways);
        if (points.Count == 0)
        {
            return null;
        }

        // Mean of unit vectors, not of raw degrees: correct across the antimeridian.
        double x = 0, y = 0, z = 0;
        foreach (var point in points)
        {
            var lat = point.LatitudeDeg * Math.PI / 180;
            var lon = point.LongitudeDeg * Math.PI / 180;
            x += Math.Cos(lat) * Math.Cos(lon);
            y += Math.Cos(lat) * Math.Sin(lon);
            z += Math.Sin(lat);
        }

        var centre = GeoPoint.FromRaw(
            Math.Atan2(z, Math.Sqrt((x * x) + (y * y))) * 180 / Math.PI,
            Math.Atan2(y, x) * 180 / Math.PI);
        if (centre is null)
        {
            return null;
        }

        // Runway points of one airport sit within a few miles of each other. A wider spread
        // means the wire shape is not what this reduction assumes — say nothing rather than
        // publish a centre in the wrong country; the DFD tier answers instead.
        if (points.Any(point => GreatCircle.DistanceNm(centre.Value, point) > MaxSpreadNm))
        {
            return null;
        }

        var elevations = runways!
            .Select(runway => ParseFeet(runway.ElevationFt))
            .Where(feet => feet is not null)
            .Select(feet => feet!.Value)
            .ToList();
        return new AirportLocation(icao, centre.Value, elevations.Count > 0 ? elevations.Average() : null, "gateway");
    }

    private static List<GeoPoint> Points(IReadOnlyList<RunwayResponse>? runways)
    {
        var points = new List<GeoPoint>();
        foreach (var runway in runways ?? [])
        {
            // The other reading of the DTO: "lat" carries only the latitude and "lng" only
            // the longitude. Two half-filled pairs are ONE point, not two bogus ones.
            if (runway.Lat is { Longitude: 0 } latOnly && runway.Lng is { Latitude: 0 } lonOnly)
            {
                if (GeoPoint.FromRaw(latOnly.Latitude, lonOnly.Longitude) is { } split)
                {
                    points.Add(split);
                }

                continue;
            }

            foreach (var candidate in new[] { runway.Lat, runway.Lng })
            {
                if (candidate is not null && GeoPoint.FromRaw(candidate.Latitude, candidate.Longitude) is { } point)
                {
                    points.Add(point);
                }
            }
        }

        return points;
    }

    /// <summary>The gateway sends elevation as a string ("83", sometimes with a unit).</summary>
    private static double? ParseFeet(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var cleaned = new string(text.Where(c => char.IsAsciiDigit(c) || c is '.' or '-').ToArray());
        return double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var feet) ? feet : null;
    }
}
