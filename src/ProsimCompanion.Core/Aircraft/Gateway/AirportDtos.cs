namespace ProsimCompanion.Core.Aircraft.Gateway;

// Wire DTOs for /efb/airport/{icao}/* and /efb/failures. Same serialization convention as the
// performance DTOs (PascalCase writes, case-insensitive camelCase reads).

/// <summary>One runway from <c>GET /efb/airport/{icao}/runways</c>.</summary>
public sealed class RunwayResponse
{
    public string? RunwayId { get; set; }
    public int? LengthFt { get; set; }
    public string? WidthFt { get; set; }
    public string? ElevationFt { get; set; }
    public string? HdgDegT { get; set; }
    public int? TransAlt { get; set; }
    public int? ThrustReductionHeight { get; set; }
    public int? AccelerationHeight { get; set; }
    public int? EngineOutAccelerationHeight { get; set; }
    public LatLng? Lat { get; set; }
    public LatLng? Lng { get; set; }

    /// <summary>Displaced threshold (string in the gateway DTO).</summary>
    public string? Dt { get; set; }

    public IntersectionViewModel[]? Intersections { get; set; }
}

public sealed class LatLng
{
    public double Latitude { get; set; }
    public double Longitude { get; set; }
}

public sealed class IntersectionViewModel
{
    public string? Name { get; set; }
    public int? ToraFt { get; set; }
    public int? TodaFt { get; set; }
    public int? AsdaFt { get; set; }
    public LatLng? Position { get; set; }
}

/// <summary>Response of <c>GET /efb/airport/{icao}/metar</c> (204 = no data).</summary>
public sealed class Metar
{
    public string MetarText { get; set; } = string.Empty;
    public string? WindDir { get; set; }
    public string? WindSpeed { get; set; }
    public int Temperature { get; set; }

    /// <summary>hPa on the gateway's ×100 convention — verify against first live data.</summary>
    public int Altim { get; set; }

    public string? Taf { get; set; }
    public string? FlightCategory { get; set; }
    public int AltimInHg { get; set; }
    public string? ObservationTime { get; set; }
}

/// <summary>
/// Outcome of the gateway METAR fetch (issue #62). Three states:
/// <list type="bullet">
/// <item><see cref="Metar"/> non-null — an observation was returned;</item>
/// <item><see cref="Metar"/> null, <see cref="FailureReason"/> null — the gateway answered
///   204: it authoritatively has no METAR for the ICAO;</item>
/// <item><see cref="FailureReason"/> non-null — the fetch itself failed (unreachable, HTTP
///   error, unparseable body). The 2026-08-16 flight had this endpoint 500 deterministically;
///   collapsing that to a bare null hid the difference from "no data".</item>
/// </list>
/// </summary>
public sealed record MetarFetchResult(Metar? Metar, string? FailureReason)
{
    /// <summary>True when the endpoint was consulted successfully (observation or a real 204).</summary>
    public bool Succeeded => FailureReason is null;
}

/// <summary>One failure entry from <c>GET /efb/failures</c>; also embedded in the performance
/// calculation requests.</summary>
public sealed class FailuresResponse
{
    public string? InternalName { get; set; }
    public string? Title { get; set; }
    public string? Lvl1 { get; set; }
    public string? Lvl2 { get; set; }
    public string? Lvl3 { get; set; }
    public List<FailuresResponse> SubItems { get; set; } = [];
}
