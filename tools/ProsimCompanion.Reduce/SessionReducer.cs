using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.Core.Debrief;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Logging;

namespace ProsimCompanion.Reduce;

/// <summary>One parsed session line kept in memory for the reducer and the probes.</summary>
public sealed record SessionLine(DateTimeOffset? At, string Type, string Raw, JsonElement Payload);

/// <summary>A session file loaded once: its lines plus the derived phase-at-time index.</summary>
public sealed class LoadedSession
{
    public LoadedSession(string path, IReadOnlyList<SessionLine> lines)
    {
        Path = path;
        Lines = lines;
        Timed = [.. lines.Where(l => l.At is not null)];
        FirstEvent = Timed.Count > 0 ? Timed.Min(l => l.At) : null;
        LastEvent = Timed.Count > 0 ? Timed.Max(l => l.At) : null;
    }

    public string Path { get; }
    public string File => System.IO.Path.GetFileName(Path);
    public IReadOnlyList<SessionLine> Lines { get; }
    public IReadOnlyList<SessionLine> Timed { get; }
    public DateTimeOffset? FirstEvent { get; }
    public DateTimeOffset? LastEvent { get; }

    /// <summary>Loads a session file; unparseable lines are skipped (a torn trailing line is
    /// normal on a live file).</summary>
    public static LoadedSession Load(string path)
    {
        var lines = new List<SessionLine>();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                DateTimeOffset? at = root.TryGetProperty("timestamp", out var tsEl)
                    && tsEl.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(tsEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                        ? parsed
                        : null;
                var payload = root.TryGetProperty("payload", out var p) ? p.Clone() : default;
                lines.Add(new SessionLine(at, typeEl.GetString()!, line, payload));
            }
            catch (JsonException)
            {
                // skipped
            }
        }

        return new LoadedSession(path, lines);
    }
}

/// <summary>
/// Per-session reduction: the same <see cref="DebriefFactExtractor"/> the app uses, plus the
/// header, phase timeline, downsampled flight samples, histograms, clustered log events,
/// and stall gaps. Everything is derived; raw samples are never emitted.
/// </summary>
public static class SessionReducer
{
    /// <summary>A gap between consecutive events longer than this, while the flight is live,
    /// means the app (or the sim) stalled — the recorder writes at least every 10 s.</summary>
    public const double GapSeconds = 30;

    private static readonly HashSet<string> AirbornePhases = new(StringComparer.Ordinal)
    {
        "InitialClimb", "Climb", "Cruise", "Descent", "Approach",
    };

    private static readonly HashSet<string> DensePhases = new(StringComparer.Ordinal)
    {
        "TakeoffRoll", "InitialClimb", "Approach", "LandingRollout",
    };

    public static SessionReport Reduce(LoadedSession session, IReadOnlyList<CmTraceEntry> cmTrace)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(cmTrace);

        var facts = new DebriefFactExtractor(NullLogger<DebriefFactExtractor>.Instance).Extract(session.Path);
        var header = Header(session);
        var timeline = new List<PhaseEdge>();
        var byType = new Dictionary<string, int>(StringComparer.Ordinal);
        var byPhase = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        var logOccurrences = new List<LogOccurrence>();
        var gaps = new List<GapReport>();
        var samples = new SampleAccumulator();

        var phase = "Unknown";
        var endedCleanly = false;
        DateTimeOffset? firstSample = null, lastSample = null;
        SessionLine? previous = null;

        // Pass 1: phases, histograms, samples, log events — in file order.
        foreach (var line in session.Lines)
        {
            byType[line.Type] = byType.GetValueOrDefault(line.Type) + 1;
            if (!byPhase.TryGetValue(phase, out var perPhase))
            {
                perPhase = new Dictionary<string, int>(StringComparer.Ordinal);
                byPhase[phase] = perPhase;
            }

            perPhase[line.Type] = perPhase.GetValueOrDefault(line.Type) + 1;

            switch (line.Type)
            {
                case "phase-changed":
                    var to = Str(line.Payload, "current") ?? "Unknown";
                    if (line.At is { } at)
                    {
                        timeline.Add(new PhaseEdge(at, Str(line.Payload, "previous") ?? phase, to, Str(line.Payload, "rule"), Str(line.Payload, "reason")));
                    }

                    phase = to;
                    break;

                case "flight-sample":
                    if (line.At is { } sampleAt)
                    {
                        firstSample ??= sampleAt;
                        lastSample = sampleAt;
                        samples.Add(sampleAt, line.Payload, Str(line.Payload, "ph") ?? phase);
                    }

                    break;

                case "log.warning" or "log.error" or "log.fatal":
                    if (line.At is { } logAt)
                    {
                        logOccurrences.Add(new LogOccurrence(
                            logAt,
                            line.Type["log.".Length..] is var lvl ? char.ToUpperInvariant(lvl[0]) + lvl[1..] : "Warning",
                            Str(line.Payload, "component") ?? "?",
                            Str(line.Payload, "message") ?? "",
                            Str(line.Payload, "exceptionType"),
                            phase));
                    }

                    break;

                case "session-ended":
                    endedCleanly = true;
                    break;
            }

            previous = line;
        }

        // Pass 2: gaps while live (between the first and last flight sample).
        if (firstSample is { } liveFrom && lastSample is { } liveTo)
        {
            SessionLine? prior = null;
            foreach (var line in session.Timed)
            {
                if (prior?.At is { } a && line.At is { } b && a >= liveFrom && a <= liveTo)
                {
                    var seconds = (b - a).TotalSeconds;
                    if (seconds > GapSeconds)
                    {
                        gaps.Add(new GapReport(a, b, Math.Round(seconds, 1), prior.Type, line.Type));
                    }
                }

                prior = line;
            }
        }

        // CMTrace fallback: attribute by time range, phase from the timeline.
        var cmOccurrences = new List<LogOccurrence>();
        if (session.FirstEvent is { } first && session.LastEvent is { } last)
        {
            foreach (var entry in cmTrace)
            {
                if (entry.Severity >= CmTraceFormat.SeverityWarning && entry.Timestamp >= first && entry.Timestamp <= last)
                {
                    cmOccurrences.Add(new LogOccurrence(
                        entry.Timestamp, entry.Level, entry.Component, entry.Message, null, PhaseAt(timeline, entry.Timestamp)));
                }
            }
        }

        return new SessionReport(
            session.File,
            session.FirstEvent,
            session.LastEvent,
            session.Lines.Count,
            endedCleanly,
            header,
            facts,
            timeline,
            samples.Summarize(),
            byType,
            byPhase.ToDictionary(p => p.Key, p => (IReadOnlyDictionary<string, int>)p.Value, StringComparer.Ordinal),
            LogClusterer.Cluster(logOccurrences),
            LogClusterer.Cluster(cmOccurrences),
            gaps);
    }

    /// <summary>True when the CMTrace entry falls inside this session's time range.</summary>
    public static bool Covers(LoadedSession session, DateTimeOffset at)
        => session.FirstEvent is { } first && session.LastEvent is { } last && at >= first && at <= last;

    private static string PhaseAt(IReadOnlyList<PhaseEdge> timeline, DateTimeOffset at)
    {
        var phase = "Unknown";
        foreach (var edge in timeline)
        {
            if (edge.At > at)
            {
                break;
            }

            phase = edge.To;
        }

        return phase;
    }

    private static SessionHeaderReport Header(LoadedSession session)
    {
        var first = session.Lines.FirstOrDefault(l => l.Type is SessionHeader.StartedEvent or SessionHeader.RotatedEvent);
        if (first is null || first.Payload.ValueKind != JsonValueKind.Object)
        {
            return new SessionHeaderReport("unversioned", null, null, null, null, null, null, null);
        }

        var p = first.Payload;
        return new SessionHeaderReport(
            first.Type,
            Str(p, "appVersion"),
            Str(p, "commit"),
            Str(p, "runtime"),
            Str(p, "os"),
            Str(p, "architecture"),
            Num(p, "sampleIntervalSeconds"),
            Str(p, "activeProfile"));
    }

    private static string? Str(JsonElement payload, string name)
        => payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static double? Num(JsonElement payload, string name)
        => payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number
            ? el.GetDouble()
            : null;

    /// <summary>Downsamples flight samples: one point per 30 s airborne, per 5 s in the dense
    /// phases, none on the ground otherwise; min/max with timestamps for alt, ias, vs, ra.</summary>
    private sealed class SampleAccumulator
    {
        private readonly List<SamplePoint> _points = [];
        private readonly Tracker _alt = new(), _ias = new(), _vs = new(), _ra = new();
        private DateTimeOffset? _lastPoint;
        private int _raw;

        public void Add(DateTimeOffset at, JsonElement payload, string phase)
        {
            _raw++;
            var alt = Num(payload, "alt") ?? 0;
            var ias = Num(payload, "ias") ?? 0;
            var vs = Num(payload, "vs") ?? 0;
            var ra = Num(payload, "ra") ?? 0;
            var gs = Num(payload, "gs") ?? 0;
            _alt.Track(alt, at);
            _ias.Track(ias, at);
            _vs.Track(vs, at);
            _ra.Track(ra, at);

            double? interval = DensePhases.Contains(phase) ? 5 : AirbornePhases.Contains(phase) ? 30 : null;
            if (interval is null)
            {
                return;
            }

            if (_lastPoint is null || (at - _lastPoint.Value).TotalSeconds >= interval)
            {
                _points.Add(new SamplePoint(at, phase, alt, ias, vs, ra, gs));
                _lastPoint = at;
            }
        }

        public SampleSummary Summarize()
            => new(_raw, _points, _alt.Result, _ias.Result, _vs.Result, _ra.Result);

        private sealed class Tracker
        {
            private double _min = double.MaxValue, _max = double.MinValue;
            private DateTimeOffset? _minAt, _maxAt;

            public void Track(double value, DateTimeOffset at)
            {
                if (value < _min)
                {
                    _min = value;
                    _minAt = at;
                }

                if (value > _max)
                {
                    _max = value;
                    _maxAt = at;
                }
            }

            public Extreme? Result => _minAt is null ? null : new Extreme(_min, _minAt, _max, _maxAt);
        }
    }
}
