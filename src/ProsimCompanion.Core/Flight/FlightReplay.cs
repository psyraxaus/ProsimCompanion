using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Flight;

/// <summary>One transition the replayed engine committed.</summary>
public sealed record ReplayCommit(DateTimeOffset At, FlightPhase Previous, FlightPhase Current, string RuleId, string Reason)
{
    /// <summary>The timeline form used in expected-timeline sidecar files.</summary>
    public string Edge => $"{Previous} -> {Current}";
}

/// <summary>One transition the live engine recorded in the session (<c>phase-changed</c>).</summary>
public sealed record RecordedCommit(DateTimeOffset At, FlightPhase Previous, FlightPhase Current, string? Reason)
{
    /// <summary>The timeline form used in expected-timeline sidecar files.</summary>
    public string Edge => $"{Previous} -> {Current}";
}

/// <summary>The outcome of replaying one session recording.</summary>
public sealed class FlightReplayResult
{
    internal FlightReplayResult(
        IReadOnlyList<ReplayCommit> commits,
        IReadOnlyList<RecordedCommit> recorded,
        int sampleCount,
        DateTimeOffset? firstSampleAt,
        DateTimeOffset? lastSampleAt)
    {
        Commits = commits;
        RecordedCommits = recorded;
        SampleCount = sampleCount;
        FirstSampleAt = firstSampleAt;
        LastSampleAt = lastSampleAt;
    }

    /// <summary>What this build's rule table decided, in order.</summary>
    public IReadOnlyList<ReplayCommit> Commits { get; }

    /// <summary>What the live engine decided during the recording, in order (only the
    /// transitions after the first sample — earlier ones had no sample evidence).</summary>
    public IReadOnlyList<RecordedCommit> RecordedCommits { get; }

    public int SampleCount { get; }
    public DateTimeOffset? FirstSampleAt { get; }
    public DateTimeOffset? LastSampleAt { get; }

    /// <summary>The replayed phase timeline as one edge per line — the expected-timeline
    /// sidecar format.</summary>
    public string Timeline => string.Join('\n', Commits.Select(c => c.Edge));

    /// <summary>Index of the first edge where the replay and the recording disagree, or null
    /// when they match edge for edge.</summary>
    public int? FirstDivergence
    {
        get
        {
            var n = Math.Max(Commits.Count, RecordedCommits.Count);
            for (var i = 0; i < n; i++)
            {
                if (i >= Commits.Count || i >= RecordedCommits.Count || Commits[i].Edge != RecordedCommits[i].Edge)
                {
                    return i;
                }
            }

            return null;
        }
    }

    /// <summary>Human-readable report: every replayed commit with time, rule and reason,
    /// then the divergence verdict against the recording.</summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"{SampleCount} samples");
        if (FirstSampleAt is { } first && LastSampleAt is { } last)
        {
            sb.Append(CultureInfo.InvariantCulture, $" from {first:HH:mm:ss} to {last:HH:mm:ss}");
        }

        sb.AppendLine();
        foreach (var c in Commits)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{c.At:HH:mm:ss}  {c.Edge,-32} [{c.RuleId}] {c.Reason}");
        }

        sb.Append(FirstDivergence is { } i
            ? $"DIVERGES from the recording at edge #{i + 1}: replay {(i < Commits.Count ? Commits[i].Edge : "(none)")} vs recorded {(i < RecordedCommits.Count ? RecordedCommits[i].Edge : "(none)")}"
            : $"matches the recording ({RecordedCommits.Count} edges)");
        return sb.ToString();
    }
}

/// <summary>
/// Replays a session JSONL recording through a fresh <see cref="FlightStateEngine"/>: every
/// <c>flight-sample</c> event becomes the engine's input at its recorded time, with the
/// 250 ms tick cadence re-created between samples so debounces behave exactly as they did
/// live. The result carries this build's commits next to the live engine's recorded
/// <c>phase-changed</c> edges, so a rule change is judged against real flights before it
/// ever flies (Prosim2FO's replay harness, brought back).
/// </summary>
public static class FlightReplay
{
    /// <summary>Longer than this between two samples is a recording gap (app restart, not
    /// live): the engine is not tick-filled across it.</summary>
    private static readonly TimeSpan MaxFillGap = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions PayloadOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Replays a session file.</summary>
    public static FlightReplayResult RunFile(string path, FlightStateOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Run(File.ReadLines(path), options);
    }

    /// <summary>Replays session-log lines (one JSON envelope per line; other event types are
    /// ignored, malformed lines are skipped).</summary>
    public static FlightReplayResult Run(IEnumerable<string> jsonlLines, FlightStateOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(jsonlLines);
        options ??= FlightStateOptions.Default;

        var samples = new List<(DateTimeOffset At, FlightSample Sample)>();
        var recorded = new List<RecordedCommit>();
        // Ground-ops edges the live engine received as signals, not samples. Re-derived from
        // the session's GSX service events so recordings made before the sample carried the
        // boarding latch (2026-09-20) still exercise the Departure rule.
        var boardingStarts = new List<DateTimeOffset>();
        foreach (var line in jsonlLines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeEl) || !root.TryGetProperty("timestamp", out var tsEl)
                    || !DateTimeOffset.TryParse(tsEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at))
                {
                    continue;
                }

                var type = typeEl.GetString();
                if (type == "flight-sample" && root.TryGetProperty("payload", out var payload))
                {
                    var sample = payload.Deserialize<FlightSample>(PayloadOptions);
                    if (sample is not null)
                    {
                        samples.Add((at, sample));
                    }
                }
                else if (type == "phase-changed" && root.TryGetProperty("payload", out var change))
                {
                    if (TryPhase(change, "previous", out var previous) && TryPhase(change, "current", out var current))
                    {
                        var reason = change.TryGetProperty("reason", out var r) ? r.GetString() : null;
                        recorded.Add(new RecordedCommit(at, previous, current, reason));
                    }
                }
                else if (type == "gsx-service" && root.TryGetProperty("payload", out var service)
                    && service.TryGetProperty("service", out var serviceEl) && service.TryGetProperty("event", out var eventEl)
                    && string.Equals(serviceEl.GetString(), "Boarding", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(eventEl.GetString(), "Active", StringComparison.OrdinalIgnoreCase))
                {
                    boardingStarts.Add(at);
                }
            }
            catch (JsonException)
            {
                // A torn last line (app killed mid-write) must not sink the whole replay.
            }
        }

        samples.Sort((a, b) => a.At.CompareTo(b.At));
        var commits = new List<ReplayCommit>();
        if (samples.Count == 0)
        {
            return new FlightReplayResult(commits, recorded, 0, null, null);
        }

        var session = new SimSessionStore();
        session.Publish(new SimSessionSnapshot(SimSessionPhase.InSession, SimRunning: true, Paused: false, CameraState: null, SimVersion: null));
        using var engine = new FlightStateEngine(
            new HeldSource(), session, NullLogger<FlightStateEngine>.Instance,
            new FixedOptionsMonitor<FlightStateOptions>(options));

        var now = samples[0].At;
        engine.PhaseChanged += (_, e) => commits.Add(new ReplayCommit(now, e.Previous, e.Current, e.RuleId, e.Reason));

        boardingStarts.Sort();
        var nextSignal = 0;
        FlightDataSnapshot? held = null;
        foreach (var (at, sample) in samples)
        {
            if (held is not null)
            {
                // Re-create the live tick cadence between samples with the last known state.
                var gap = at - now;
                if (gap <= MaxFillGap)
                {
                    for (var t = now + FlightStateEngine.TickInterval; t < at; t += FlightStateEngine.TickInterval)
                    {
                        now = t;
                        RaiseSignalsDue(engine, boardingStarts, ref nextSignal, t);
                        engine.ProcessTick(held, t);
                    }
                }
            }

            held = sample.ToSnapshot();
            now = at;
            RaiseSignalsDue(engine, boardingStarts, ref nextSignal, at);
            engine.ProcessTick(held, at);
        }

        var firstAt = samples[0].At;
        var recordedInWindow = recorded.Where(c => c.At >= firstAt).ToList();
        return new FlightReplayResult(commits, recordedInWindow, samples.Count, firstAt, samples[^1].At);
    }

    /// <summary>Delivers every boarding-started signal stamped at or before <paramref name="now"/>
    /// to the engine, in order, exactly once — the same latch the live relay would have set.</summary>
    private static void RaiseSignalsDue(FlightStateEngine engine, List<DateTimeOffset> boardingStarts, ref int next, DateTimeOffset now)
    {
        while (next < boardingStarts.Count && boardingStarts[next] <= now)
        {
            engine.NotifyBoardingStarted();
            next++;
        }
    }

    private static bool TryPhase(JsonElement payload, string name, out FlightPhase phase)
    {
        phase = FlightPhase.Unknown;
        return payload.TryGetProperty(name, out var el)
            && el.ValueKind == JsonValueKind.String
            && Enum.TryParse(el.GetString(), ignoreCase: true, out phase);
    }

    /// <summary>The engine's timer is never started during replay; the source only exists to
    /// satisfy the constructor.</summary>
    private sealed class HeldSource : IFlightDataSource
    {
        public FlightDataSnapshot Sample() => new() { IsValid = false };
    }
}
