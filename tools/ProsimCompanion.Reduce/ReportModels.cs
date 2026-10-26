using System.Text.Json;
using System.Text.Json.Serialization;
using ProsimCompanion.Core.Debrief;

namespace ProsimCompanion.Reduce;

/// <summary>The report document: <c>{ bundle, sessions, probes, forLlm }</c>. Every type here
/// is a plain record so the JSON shape IS the schema documented in docs/agents/support-bundle.md.</summary>
public sealed record ReduceReport(
    BundleSummary Bundle,
    IReadOnlyList<SessionReport> Sessions,
    IReadOnlyList<RestartReport> Restarts,
    IReadOnlyList<LogCluster> UnattributedLogClusters,
    IReadOnlyDictionary<string, ProbeResult> Probes,
    IReadOnlyList<ProbeForLlm> ForLlm)
{
    /// <summary>The ECAM diagnostics state at export time (0.8.0-rc.10+): null when the bundle
    /// predates the pages, which the LLM step must read as "unknown", not "healthy".</summary>
    public HealthSummary? Health { get; init; }

    /// <summary>Compact on purpose: the document is LLM input under a size budget, and
    /// indentation would spend a third of it on whitespace.</summary>
    public static JsonSerializerOptions Json { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };
}

public sealed record BundleSummary(
    string Source,
    string? Folder,
    DateTimeOffset? CreatedUtc,
    string? AppVersion,
    string? Commit,
    int FileCount,
    int SessionFileCount,
    int LogFileCount,
    bool HashesVerified,
    IReadOnlyList<string> Truncated,
    IReadOnlyList<string> Notes);

/// <summary>The first line's payload (session-started / session-rotated) or "unversioned".</summary>
public sealed record SessionHeaderReport(
    string Status,
    string? AppVersion,
    string? Commit,
    string? Runtime,
    string? Os,
    string? Architecture,
    double? SampleIntervalSeconds,
    string? ActiveProfile);

public sealed record PhaseEdge(DateTimeOffset At, string From, string To, string? Rule, string? Reason);

public sealed record SamplePoint(DateTimeOffset T, string Ph, double Alt, double Ias, double Vs, double Ra, double Gs);

public sealed record Extreme(double Min, DateTimeOffset? MinAt, double Max, DateTimeOffset? MaxAt);

public sealed record SampleSummary(
    int RawSamples,
    IReadOnlyList<SamplePoint> Points,
    Extreme? Alt,
    Extreme? Ias,
    Extreme? Vs,
    Extreme? Ra);

public sealed record LogCluster(
    string Level,
    string Component,
    string Pattern,
    int Count,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    string? Phase,
    string Example,
    string? ExceptionType);

public sealed record GapReport(DateTimeOffset From, DateTimeOffset To, double Seconds, string BeforeType, string AfterType);

/// <summary>One command candidate from the <c>voice.unmatched</c> events (issue #112): a
/// phrase the pilot said that nothing acted on, with how often and where.</summary>
public sealed record VoiceCandidate(string Text, int Count, IReadOnlyList<string> Contexts, DateTimeOffset? LastHeard);

/// <summary>The heard-but-not-understood summary of a session: totals and the repeated
/// phrases (count ≥ 2, most frequent first, at most <see cref="SessionReducer.MaxVoiceCandidates"/>).
/// Sterile-phase absorptions are counted in <see cref="Suppressed"/> and never listed.</summary>
public sealed record VoiceUnmatchedReport(int Total, int Suppressed, IReadOnlyList<VoiceCandidate> Candidates);

public sealed record SessionReport(
    string File,
    DateTimeOffset? FirstEvent,
    DateTimeOffset? LastEvent,
    int EventCount,
    bool EndedCleanly,
    SessionHeaderReport Header,
    DebriefFacts Facts,
    IReadOnlyList<PhaseEdge> PhaseTimeline,
    SampleSummary Samples,
    IReadOnlyDictionary<string, int> EventsByType,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> EventsByTypePerPhase,
    IReadOnlyList<LogCluster> LogEvents,
    IReadOnlyList<LogCluster> CmTraceEvents,
    IReadOnlyList<GapReport> Gaps,
    VoiceUnmatchedReport VoiceUnmatched)
{
    /// <summary>Which stream <see cref="LogEvents"/>/<see cref="CmTraceEvents"/> came from, so the
    /// LLM step knows whether an empty list means "clean" or "not mirrored".</summary>
    public string LogSource => LogEvents.Count > 0 ? "session" : CmTraceEvents.Count > 0 ? "cmtrace" : "none";
}

public sealed record RestartReport(
    string PreviousFile,
    string File,
    DateTimeOffset? PreviousLastEvent,
    string? PreviousLastType,
    DateTimeOffset? StartedAt);

public sealed record ProbeResult(
    string Kind,
    int Issue,
    string State,
    string Verdict,
    IReadOnlyList<string> Evidence);

/// <summary>A probe the reducer could not evaluate; its text is passed through untouched.</summary>
public sealed record ProbeForLlm(
    string Id,
    int Issue,
    string State,
    string Kind,
    IReadOnlyList<string> Sources,
    IReadOnlyList<string> Checks,
    string? Notes);

/// <summary>One STATUS-page caution as the bundle carried it.</summary>
public sealed record HealthCaution(string System, string Title, string Severity, IReadOnlyList<string> Actions, DateTimeOffset RaisedUtc);

/// <summary>One RECENT line from <c>diagnostics-events.jsonl</c>.</summary>
public sealed record HealthEvent(DateTimeOffset AtUtc, string Text, string State);

/// <summary>
/// The <c>health</c> section: what the diagnostics pages showed when the user exported the
/// bundle. <see cref="Cautions"/>, <see cref="Inop"/> and <see cref="Degraded"/> are the STATUS
/// page's three lists; <see cref="Recent"/> is its change log (oldest first); the node table
/// lists every non-green node by page so "which box was amber" is one lookup.
/// </summary>
public sealed record HealthSummary(
    IReadOnlyList<HealthCaution> Cautions,
    IReadOnlyList<string> Inop,
    IReadOnlyList<string> Degraded,
    DateTimeOffset? LastSnapshotUtc,
    IReadOnlyDictionary<string, string> AbnormalNodes,
    IReadOnlyList<HealthEvent> Recent,
    IReadOnlyList<string> Notes);
