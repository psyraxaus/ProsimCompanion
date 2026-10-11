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
        DateTimeOffset? lastSampleAt,
        int pauseRoundTrips = 0)
    {
        Commits = commits;
        RecordedCommits = recorded;
        SampleCount = sampleCount;
        FirstSampleAt = firstSampleAt;
        LastSampleAt = lastSampleAt;
        PauseRoundTrips = pauseRoundTrips;
    }

    /// <summary>How many 'sim-paused' edges the harness fed through the session store (issue
    /// #168) — the proof that a recording's pause path was exercised, not just absent.</summary>
    public int PauseRoundTrips { get; }

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

        if (PauseRoundTrips > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $", {PauseRoundTrips} pause round-trip(s) replayed");
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
        // Edges the live engine received as signals, not samples: the GSX Boarding
        // Active/Completed events (re-derived so recordings made before the sample carried the
        // boarding latch, 2026-09-20, still exercise the Departure rule — and so the
        // ground-ops layer's BoardingUnderway latch is real for the re-raise after a reset)
        // and the MSFS pause round-trip (sim-paused / sim-resumed, issue #168) that the live
        // engine saw through the session store.
        var signals = new List<ReplaySignal>();
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
                    && string.Equals(serviceEl.GetString(), "Boarding", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.Equals(eventEl.GetString(), "Active", StringComparison.OrdinalIgnoreCase))
                    {
                        signals.Add(new ReplaySignal(at, ReplaySignalKind.BoardingStarted));
                    }
                    else if (string.Equals(eventEl.GetString(), "Completed", StringComparison.OrdinalIgnoreCase))
                    {
                        signals.Add(new ReplaySignal(at, ReplaySignalKind.BoardingCompleted));
                    }
                }
                else if (type == "sim-paused")
                {
                    signals.Add(new ReplaySignal(at, ReplaySignalKind.SimPaused));
                }
                else if (type == "sim-resumed")
                {
                    signals.Add(new ReplaySignal(at, ReplaySignalKind.SimResumed));
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
        session.Publish(InSession);
        var groundOps = new GroundOpsSignals();
        using var engine = new FlightStateEngine(
            new HeldSource(), session, NullLogger<FlightStateEngine>.Instance,
            new FixedOptionsMonitor<FlightStateOptions>(options), groundOps);

        var now = samples[0].At;
        engine.PhaseChanged += (_, e) => commits.Add(new ReplayCommit(now, e.Previous, e.Current, e.RuleId, e.Reason));

        signals.Sort((a, b) => a.At.CompareTo(b.At));
        var nextSignal = 0;
        var pauses = 0;
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
                        RaiseSignalsDue(engine, session, groundOps, signals, ref nextSignal, ref pauses, t, held);
                        engine.ProcessTick(held, t);
                    }
                }
            }

            held = sample.ToSnapshot();
            now = at;
            RaiseSignalsDue(engine, session, groundOps, signals, ref nextSignal, ref pauses, at, held);
            engine.ProcessTick(held, at);
        }

        var firstAt = samples[0].At;
        var recordedInWindow = recorded.Where(c => c.At >= firstAt).ToList();
        return new FlightReplayResult(commits, recordedInWindow, samples.Count, firstAt, samples[^1].At, pauses);
    }

    private static readonly SimSessionSnapshot InSession =
        new(SimSessionPhase.InSession, SimRunning: true, Paused: false, CameraState: null, SimVersion: null);

    /// <summary>The Escape menu as the session monitor reports it: camera 34, sim running,
    /// Pause_EX1 flags set (CMTrace 2026-10-11 10:35:16 local).</summary>
    private static readonly SimSessionSnapshot PausedOutOfSession =
        new(SimSessionPhase.NotInSession, SimRunning: true, Paused: true, CameraState: 34, SimVersion: null);

    private enum ReplaySignalKind
    {
        BoardingStarted,
        BoardingCompleted,
        SimPaused,
        SimResumed,
    }

    private sealed record ReplaySignal(DateTimeOffset At, ReplaySignalKind Kind);

    /// <summary>Delivers every signal stamped at or before <paramref name="now"/>, in order,
    /// exactly once: boarding edges through the ground-ops hub (the same latch the live relay
    /// would have set) and pause edges through the session store. A session edge is followed
    /// by one engine tick at its own time with the held sample, so the engine registers the
    /// pause even when the recording has no sample between pause and resume (the recorder
    /// stops while not live) or the gap is too long to tick-fill.</summary>
    private static void RaiseSignalsDue(
        FlightStateEngine engine, SimSessionStore session, GroundOpsSignals groundOps,
        List<ReplaySignal> signals, ref int next, ref int pauses, DateTimeOffset now, FlightDataSnapshot? held)
    {
        while (next < signals.Count && signals[next].At <= now)
        {
            var signal = signals[next++];
            switch (signal.Kind)
            {
                case ReplaySignalKind.BoardingStarted:
                    groundOps.RaiseBoardingStarted();
                    break;
                case ReplaySignalKind.BoardingCompleted:
                    groundOps.RaiseBoardingCompleted();
                    break;
                case ReplaySignalKind.SimPaused:
                    pauses++;
                    session.Publish(PausedOutOfSession);
                    if (held is not null)
                    {
                        engine.ProcessTick(held, signal.At);
                    }

                    break;
                case ReplaySignalKind.SimResumed:
                    session.Publish(InSession);
                    if (held is not null)
                    {
                        engine.ProcessTick(held, signal.At);
                    }

                    break;
            }
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
