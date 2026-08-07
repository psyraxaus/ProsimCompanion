namespace ProsimCompanion.Core.Logbook;

/// <summary>One flight's persisted logbook record. Keyed by <see cref="SessionId"/> (the
/// session log's file name) so re-folding a session never double-counts.</summary>
public sealed class LogbookFlight
{
    public string SessionId { get; set; } = "";

    /// <summary>Flight date (ISO, from the session id's timestamp), e.g. "2026-08-08".</summary>
    public string Date { get; set; } = "";

    public string? Origin { get; set; }

    public string? Destination { get; set; }

    public string? DepartureRunway { get; set; }

    public string? ArrivalRunway { get; set; }

    public int? BlockMinutes { get; set; }

    public int? FlightMinutes { get; set; }

    public double? LiftoffIasKt { get; set; }

    public double? TouchdownGroundSpeedKt { get; set; }

    /// <summary>True when the flight touched down (a landing).</summary>
    public bool Landed { get; set; }

    /// <summary>Overall approach verdict: "stable" | "unstable" | "indeterminate", or null
    /// when no gate was judged.</summary>
    public string? ApproachResult { get; set; }

    /// <summary>Titles of abnormals handled in the flight.</summary>
    public List<string> Abnormals { get; set; } = [];

    public int DefectsRaised { get; set; }

    public int DefectsRectified { get; set; }

    /// <summary>Open tech-log (MEL) defects carried on the flight.</summary>
    public int DefectsCarried { get; set; }
}

/// <summary>One duty day's record. Company day mode is deferred — the shape ships now so the
/// store's schema doesn't need a version bump when it arrives; <c>Days</c> stays empty.</summary>
public sealed class LogbookDay
{
    public string DayId { get; set; } = "";

    public string Date { get; set; } = "";

    public int Legs { get; set; }

    public List<string> Route { get; set; } = [];

    public int? BlockMinutes { get; set; }

    public int? DutyMinutes { get; set; }
}

/// <summary>The persisted logbook file shape (<c>logbook.json</c>).</summary>
public sealed class LogbookStore
{
    public int Version { get; set; } = 1;

    public List<LogbookFlight> Flights { get; set; } = [];

    public List<LogbookDay> Days { get; set; } = [];
}

/// <summary>Per-airport landing history, computed from the store. Note the semantics:
/// Fastest is the highest touchdown ground speed, Slowest the lowest — Prosim2FO had these
/// swapped (min in the Fastest slot) and the names silently lied; fixed here.</summary>
public sealed record AirportStat(
    string Icao,
    int Landings,
    double? FastestTouchdownGsKt,
    double? SlowestTouchdownGsKt);

/// <summary>Rolling career aggregates, computed on demand from the store — never persisted,
/// so they cannot drift out of sync with the flights list.</summary>
public sealed record LogbookAggregates(
    int TotalFlights,
    double TotalBlockHours,
    double TotalFlightHours,
    int Landings,
    int StabilizedApproaches,
    int JudgedApproaches,
    IReadOnlyList<AirportStat> Airports)
{
    /// <summary>Stabilized rate (percent) over gate-judged approaches only; null when none
    /// were judged (a rate over unjudged approaches would be an invented number).</summary>
    public double? StabilizedRatePct
        => JudgedApproaches > 0 ? Math.Round(100.0 * StabilizedApproaches / JudgedApproaches) : null;

    public static LogbookAggregates Empty { get; } = new(0, 0, 0, 0, 0, 0, []);
}
