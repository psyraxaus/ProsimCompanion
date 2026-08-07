using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Debrief;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Sessions;

namespace ProsimCompanion.Core.Logbook;

/// <summary>
/// The pilot logbook: folds a completed session's deterministic facts into a persistent
/// store, and answers career questions (aggregates, per-airport history, the debrief's
/// "landing number N into X" comparison). Independent of the spoken debrief — a flight is
/// recorded even when the debrief is disabled.
/// </summary>
public interface ILogbookService
{
    /// <summary>Recorded flights, oldest first (append order).</summary>
    IReadOnlyList<LogbookFlight> Flights { get; }

    /// <summary>Folds one session log into the store. Idempotent — keyed by session id, and a
    /// session with nothing meaningful (no landing, block time or route) is skipped.</summary>
    void FoldSession(string sessionPath);

    /// <summary>Folds every <c>session-*.jsonl</c> in the sessions folder except the
    /// in-progress one. Returns the number of flights added; safe to re-run.</summary>
    int Backfill();

    /// <summary>Career aggregates, computed on read.</summary>
    LogbookAggregates GetAggregates();

    /// <summary>One fact-locked comparison for the debrief ("That's landing number N into X."),
    /// excluding <paramref name="excludeSessionId"/> so the line is correct whether or not the
    /// current flight has already been folded. Null when the destination is unknown or the
    /// flight didn't land.</summary>
    string? DescribeComparison(DebriefFacts facts, string? excludeSessionId);
}

/// <summary>
/// File-backed <see cref="ILogbookService"/>; also the logbook
/// <see cref="ISessionFinalizationStep"/> (order 20 — after the debrief has read the log,
/// before the tech-log fold). Same robustness contract as the tech log: atomic
/// temp-then-move writes, corrupt store moved aside and rebuilt, never a throw.
/// </summary>
public sealed class LogbookService : ILogbookService, ISessionFinalizationStep
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly IDebriefFactExtractor _extractor;
    private readonly JsonlEventLog _eventLog;
    private readonly IOptionsMonitor<LogbookOptions> _options;
    private readonly ILogger<LogbookService> _logger;
    private readonly object _gate = new();

    private LogbookStore _store = new();
    private bool _started;

    public LogbookService(
        IDebriefFactExtractor extractor,
        JsonlEventLog eventLog,
        IOptionsMonitor<LogbookOptions> options,
        ILogger<LogbookService> logger)
    {
        ArgumentNullException.ThrowIfNull(extractor);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _extractor = extractor;
        _eventLog = eventLog;
        _options = options;
        _logger = logger;
    }

    string ISessionFinalizationStep.Name => "logbook";

    int ISessionFinalizationStep.Order => 20;

    public void Start()
    {
        lock (_gate)
        {
            if (_started)
            {
                return;
            }

            _started = true;
            Load();
            _logger.LogInformation("Logbook started ({Count} flight(s) on file)", _store.Flights.Count);
        }
    }

    public IReadOnlyList<LogbookFlight> Flights
    {
        get
        {
            lock (_gate)
            {
                return _store.Flights.ToList();
            }
        }
    }

    Task ISessionFinalizationStep.RunAsync(SessionFinalizationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        FoldSession(context.SessionPath);
        return Task.CompletedTask;
    }

    // ---- folding ----

    public void FoldSession(string sessionPath)
    {
        try
        {
            if (!_options.CurrentValue.Enabled
                || string.IsNullOrWhiteSpace(sessionPath)
                || !File.Exists(sessionPath))
            {
                return;
            }

            var sessionId = Path.GetFileNameWithoutExtension(sessionPath);
            lock (_gate)
            {
                if (ContainsLocked(sessionId))
                {
                    return; // idempotent
                }
            }

            var facts = _extractor.Extract(sessionPath);
            var flight = BuildFlight(sessionId, facts);
            if (flight is null)
            {
                return; // nothing worth recording
            }

            lock (_gate)
            {
                // Double-checked: extraction happens outside the lock, so a concurrent fold of
                // the same session (finalizer + backfill) must re-verify before appending.
                if (ContainsLocked(sessionId))
                {
                    return;
                }

                _store.Flights.Add(flight);
                Save();
            }

            _eventLog.Record("logbook.folded", new
            {
                sessionId,
                origin = flight.Origin,
                destination = flight.Destination,
                landed = flight.Landed,
            });
            _logger.LogInformation("Logbook: recorded {Session} {Origin}->{Destination}",
                sessionId, flight.Origin ?? "?", flight.Destination ?? "?");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Logbook fold failed for {Path}", sessionPath);
        }
    }

    public int Backfill()
    {
        var directory = Path.GetDirectoryName(_eventLog.Path);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return 0;
        }

        var currentId = Path.GetFileNameWithoutExtension(_eventLog.Path);
        var added = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "session-*.jsonl", SearchOption.TopDirectoryOnly))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            if (string.Equals(id, currentId, StringComparison.OrdinalIgnoreCase))
            {
                continue; // never fold the in-progress session
            }

            int before;
            lock (_gate)
            {
                before = _store.Flights.Count;
            }

            FoldSession(file);
            lock (_gate)
            {
                if (_store.Flights.Count > before)
                {
                    added++;
                }
            }
        }

        _logger.LogInformation("Logbook backfill added {Added} flight(s)", added);
        return added;
    }

    private static LogbookFlight? BuildFlight(string sessionId, DebriefFacts facts)
    {
        var landed = facts.TouchdownGroundSpeedKt is not null || facts.FlightMinutes is not null;
        var meaningful = landed || facts.BlockMinutes is not null
            || facts.Origin is not null || facts.Destination is not null;
        if (!meaningful)
        {
            return null;
        }

        return new LogbookFlight
        {
            SessionId = sessionId,
            Date = DateFromSessionId(sessionId),
            Origin = facts.Origin,
            Destination = facts.Destination,
            DepartureRunway = facts.DepartureRunway,
            ArrivalRunway = facts.ArrivalRunway,
            BlockMinutes = facts.BlockMinutes,
            FlightMinutes = facts.FlightMinutes,
            LiftoffIasKt = facts.LiftoffIasKt,
            TouchdownGroundSpeedKt = facts.TouchdownGroundSpeedKt,
            Landed = landed,
            ApproachResult = OverallApproach(facts),
            Abnormals = facts.Abnormals.Select(a => a.Title).ToList(),
            DefectsRaised = facts.DefectsRaised,
            DefectsRectified = facts.DefectsRectified,
            DefectsCarried = facts.DefectsCarried,
        };
    }

    private static string? OverallApproach(DebriefFacts facts)
    {
        if (facts.Gates.Count == 0)
        {
            return null;
        }

        if (facts.Gates.Any(g => string.Equals(g.Result, "unstable", StringComparison.OrdinalIgnoreCase)))
        {
            return "unstable";
        }

        if (facts.Gates.Any(g => string.Equals(g.Result, "stable", StringComparison.OrdinalIgnoreCase)))
        {
            return "stable";
        }

        return "indeterminate";
    }

    // ---- aggregates + comparison ----

    public LogbookAggregates GetAggregates()
    {
        lock (_gate)
        {
            var flights = _store.Flights;
            if (flights.Count == 0)
            {
                return LogbookAggregates.Empty;
            }

            var blockHours = flights.Sum(f => f.BlockMinutes ?? 0) / 60.0;
            var flightHours = flights.Sum(f => f.FlightMinutes ?? 0) / 60.0;
            var landings = flights.Count(f => f.Landed);
            var judged = flights.Count(f => f.ApproachResult is "stable" or "unstable");
            var stabilized = flights.Count(f => f.ApproachResult == "stable");

            var airports = flights
                .Where(f => f.Landed && !string.IsNullOrWhiteSpace(f.Destination))
                .GroupBy(f => f.Destination!, StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var speeds = g
                        .Where(f => f.TouchdownGroundSpeedKt is not null)
                        .Select(f => f.TouchdownGroundSpeedKt!.Value)
                        .ToList();

                    // Fastest = Max, Slowest = Min (the fixed semantics — see AirportStat).
                    return new AirportStat(
                        g.Key,
                        g.Count(),
                        speeds.Count > 0 ? speeds.Max() : null,
                        speeds.Count > 0 ? speeds.Min() : null);
                })
                .OrderByDescending(a => a.Landings)
                .ToList();

            return new LogbookAggregates(
                flights.Count,
                Math.Round(blockHours, 1),
                Math.Round(flightHours, 1),
                landings,
                stabilized,
                judged,
                airports);
        }
    }

    public string? DescribeComparison(DebriefFacts facts, string? excludeSessionId)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var destination = facts.Destination;
        if (string.IsNullOrWhiteSpace(destination))
        {
            return null;
        }

        var landedNow = facts.TouchdownGroundSpeedKt is not null || facts.FlightMinutes is not null;
        if (!landedNow)
        {
            return null;
        }

        lock (_gate)
        {
            var priors = _store.Flights.Count(f => f.Landed
                && string.Equals(f.Destination, destination, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(f.SessionId, excludeSessionId, StringComparison.OrdinalIgnoreCase));

            return priors == 0
                ? $"That's your first landing into {destination}."
                : $"That's landing number {priors + 1} into {destination}.";
        }
    }

    // ---- persistence ----

    private string ResolvePath()
    {
        var configured = _options.CurrentValue.Path;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ProsimCompanion",
            "logbook.json");
    }

    private void Load()
    {
        var path = ResolvePath();
        if (!File.Exists(path))
        {
            _store = new LogbookStore();
            return;
        }

        try
        {
            _store = JsonSerializer.Deserialize<LogbookStore>(File.ReadAllText(path), JsonOptions)
                ?? new LogbookStore();
        }
        catch (Exception ex)
        {
            var backup = path + $".corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss}.bak";
            try
            {
                File.Move(path, backup, overwrite: true);
            }
            catch (Exception moveEx)
            {
                // Best effort — a locked/permission-denied file just stays put; starting
                // fresh matters more than the backup.
                _logger.LogDebug(moveEx, "Could not move corrupt store aside");
            }

            _logger.LogWarning(ex, "Logbook corrupt — backed up to {Backup} and starting fresh", backup);
            _store = new LogbookStore();
        }
    }

    private void Save()
    {
        var path = ResolvePath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(_store, JsonOptions);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not write logbook {Path}", path);
        }
    }

    private bool ContainsLocked(string sessionId)
        => _store.Flights.Any(f => string.Equals(f.SessionId, sessionId, StringComparison.OrdinalIgnoreCase));

    /// <summary>"session-yyyyMMdd-HHmmss" → "yyyy-MM-dd"; empty when the id doesn't match.</summary>
    internal static string DateFromSessionId(string sessionId)
    {
        const string prefix = "session-";
        if (sessionId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && sessionId.Length >= prefix.Length + 8
            && DateTime.TryParseExact(
                sessionId.Substring(prefix.Length, 8), "yyyyMMdd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        return "";
    }
}
