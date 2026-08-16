using System.Globalization;
using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Logbook;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Logbook;

/// <summary>
/// Voice queries over the pilot logbook — Prosim2FO's deterministic answer templates over
/// this repo's <see cref="ILogbookService"/> aggregates (whose fastest/slowest touchdown
/// semantics are already corrected; the spoken range keeps slow → fast order). Fixed phrases
/// are exact-match; "landing stats for {airport}" is prefix-matched, with one grammar phrase
/// contributed per landed destination so the recognizer can actually hear the ICAO.
/// </summary>
public sealed class LogbookVoiceService : IVoiceFeature
{
    private const string StatsForPrefix = "landing stats for ";

    private static readonly string[] FixedPhrases =
    [
        "logbook summary", "read my logbook", "how many landings",
        "how many hours", "how many flights", "logbook day summary",
    ];

    private readonly ILogbookService _logbook;
    private readonly ISpeechArbiter _arbiter;
    private readonly ILogger<LogbookVoiceService> _logger;

    // Optional: with no name source the answers keep speaking the raw ICAO — issue #70.
    private readonly Core.Airports.IAirportNames? _airportNames;

    public LogbookVoiceService(
        ILogbookService logbook,
        ISpeechArbiter arbiter,
        ILogger<LogbookVoiceService> logger,
        Core.Airports.IAirportNames? airportNames = null)
    {
        ArgumentNullException.ThrowIfNull(logbook);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(logger);

        _logbook = logbook;
        _arbiter = arbiter;
        _logger = logger;
        _airportNames = airportNames;
    }

    /// <summary>Fixed phrases plus one "landing stats for {icao}" per landed destination —
    /// recomputed each read so the grammar tracks the store (predecessor behaviour).</summary>
    public IEnumerable<string> Phrases
    {
        get
        {
            var perAirport = _logbook.Flights
                .Where(f => f.Landed && !string.IsNullOrWhiteSpace(f.Destination))
                .Select(f => StatsForPrefix + f.Destination!.ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase);
            return FixedPhrases.Concat(perAirport);
        }
    }

    public bool ValueParse => false;

    public bool TryHandle(string utterance)
    {
        var text = CommandMatcher.Normalize(utterance);

        string answer;
        if (text is "logbook summary" or "read my logbook")
        {
            answer = SummaryText(_logbook.GetAggregates());
        }
        else if (text is "how many landings")
        {
            answer = LandingsText(_logbook.GetAggregates());
        }
        else if (text is "how many hours")
        {
            answer = HoursText(_logbook.GetAggregates());
        }
        else if (text is "how many flights")
        {
            answer = FlightsText(_logbook.GetAggregates());
        }
        else if (text is "logbook day summary")
        {
            answer = DaySummaryText(_logbook.Days.LastOrDefault());
        }
        else if (TryParseAirportQuery(text, out var icao))
        {
            answer = AirportText(_logbook.GetAggregates(), icao, _airportNames?.SpokenName(icao));
        }
        else
        {
            return false;
        }

        _logger.LogDebug("Logbook query \"{Query}\"", text);
        _ = _arbiter.EnqueueAsync(new SpeechRequest(answer, SpeechPriority.Normal, Tag: "logbook"));
        return true;
    }

    /// <summary>Prefix parse for "landing stats for {airport}". Pure; static for tests. The
    /// remainder must be non-blank — a bare prefix is not a query.</summary>
    public static bool TryParseAirportQuery(string normalizedText, out string icao)
    {
        icao = "";
        if (!normalizedText.StartsWith(StatsForPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        icao = normalizedText[StatsForPrefix.Length..].Trim();
        return icao.Length > 0;
    }

    // ---- deterministic answer templates (predecessor wording; numbers locked).
    // Pure and static for tests, following BuildLoadsheet's pattern. ----

    /// <summary>"Logbook. N flights, X.X block hours, N landings[, N percent stabilized
    /// approaches]." — the rate clause appears only when gates were judged (a rate over
    /// unjudged approaches would be an invented number).</summary>
    public static string SummaryText(LogbookAggregates a)
    {
        ArgumentNullException.ThrowIfNull(a);
        if (a.TotalFlights == 0)
        {
            return "Your logbook is empty. No flights recorded yet.";
        }

        var s = $"Logbook. {a.TotalFlights} flights, {Fmt(a.TotalBlockHours)} block hours, {a.Landings} landings";
        if (a.StabilizedRatePct is { } rate)
        {
            s += $", {Fmt0(rate)} percent stabilized approaches";
        }

        return s + ".";
    }

    /// <summary>Answer to "how many landings".</summary>
    public static string LandingsText(LogbookAggregates a)
    {
        ArgumentNullException.ThrowIfNull(a);
        return a.Landings == 0
            ? "No landings logged yet."
            : $"{a.Landings} landings logged across {a.TotalFlights} flights.";
    }

    /// <summary>Answer to "how many hours" — flight hours first, then block hours.</summary>
    public static string HoursText(LogbookAggregates a)
    {
        ArgumentNullException.ThrowIfNull(a);
        return a.TotalFlights == 0
            ? "No hours logged yet."
            : $"{Fmt(a.TotalFlightHours)} flight hours, and {Fmt(a.TotalBlockHours)} block hours logged.";
    }

    /// <summary>Answer to "how many flights".</summary>
    public static string FlightsText(LogbookAggregates a)
    {
        ArgumentNullException.ThrowIfNull(a);
        return a.TotalFlights == 0
            ? "No flights logged yet."
            : $"{a.TotalFlights} flights in the logbook.";
    }

    /// <summary>Answer to "logbook day summary" — the most recent duty day; every clause is
    /// optional so a sparsely-recorded day still reads naturally.</summary>
    public static string DaySummaryText(LogbookDay? day)
    {
        if (day is null)
        {
            return "No duty day recorded yet.";
        }

        var s = $"Last duty day. {day.Legs} sector{(day.Legs == 1 ? "" : "s")}";
        if (day.Route.Count >= 2)
        {
            s += $", {string.Join(" to ", day.Route)}";
        }

        if (day.BlockMinutes is { } block)
        {
            s += $". {Fmt(block / 60.0)} block hours";
        }

        if (day.DutyMinutes is { } duty)
        {
            s += $", {Fmt(duty / 60.0)} duty hours";
        }

        if (day.JudgedApproaches > 0)
        {
            s += $". {day.StabilizedApproaches} of {day.JudgedApproaches} approaches stabilized";
        }

        if (day.Abnormals > 0)
        {
            s += $". {day.Abnormals} abnormal{(day.Abnormals == 1 ? "" : "s")} handled";
        }

        if (day.DefectsRaised > 0)
        {
            s += $". {day.DefectsRaised} defect{(day.DefectsRaised == 1 ? "" : "s")} raised";
        }

        return s + ".";
    }

    /// <summary>Answer to "landing stats for {icao}". The touchdown range is spoken slow →
    /// fast, so it reads Slowest first — the aggregates' fastest/slowest naming is already
    /// corrected in this repo (see <see cref="AirportStat"/>). An optional spoken name
    /// ("Heathrow") replaces the raw ICAO (issue #70) — stays a parameter so the template
    /// remains pure/static for tests.</summary>
    public static string AirportText(LogbookAggregates a, string icao, string? spokenName = null)
    {
        ArgumentNullException.ThrowIfNull(a);
        if (string.IsNullOrWhiteSpace(icao))
        {
            return "Which airport?";
        }

        var stat = a.Airports.FirstOrDefault(
            s => string.Equals(s.Icao, icao, StringComparison.OrdinalIgnoreCase));
        if (stat is null)
        {
            return $"No landings logged into {spokenName ?? icao.ToUpperInvariant()} yet.";
        }

        var text = $"{stat.Landings} landings into {spokenName ?? stat.Icao.ToUpperInvariant()}";
        if (stat is { FastestTouchdownGsKt: { } fast, SlowestTouchdownGsKt: { } slow })
        {
            text += $", touchdown ground speed from {Fmt0(slow)} to {Fmt0(fast)} knots";
        }

        return text + ".";
    }

    // Invariant culture — a European locale must never speak "62,3".
    private static string Fmt(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);

    private static string Fmt0(double v) => v.ToString("0", CultureInfo.InvariantCulture);
}
