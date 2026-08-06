namespace ProsimCompanion.Speech.Arbiter;

/// <summary>Priority bands of the single speech queue (semantics carried from Prosim2FO's
/// proven arbiter). Order matters — comparisons like <c>&gt;= High</c> are used directly.</summary>
public enum SpeechPriority
{
    /// <summary>Advisories, fuel-check prompts, debrief chatter — anything deferrable.</summary>
    Low = 0,

    /// <summary>Checklist items, briefing content, replies to user queries — the default.</summary>
    Normal = 1,

    /// <summary>Standard flight-deck callouts (positive climb, stabilization gates). Jumps
    /// the queue but never pre-empts in-flight speech.</summary>
    High = 2,

    /// <summary>Safety callouts (minimums, V1, go around). Pre-empts anything speaking.</summary>
    Critical = 3,
}

/// <summary>
/// One utterance submitted to the arbiter. Features never call TTS directly — this is the only
/// way speech happens. There is deliberately no dedup here: repeat-prone callers own their own
/// latches/cooldowns, and a TTL + validity predicate lets a stale duplicate cancel itself.
/// </summary>
/// <param name="Text">What to say.</param>
/// <param name="Priority">Queue band; see <see cref="SpeechPriority"/>.</param>
/// <param name="Ttl">Optional shelf life measured from submission — an expired item is
/// discarded at dequeue instead of spoken late.</param>
/// <param name="IsStillValid">Optional re-check evaluated at dequeue and when leaving
/// deferral; false discards the item ("gear down" must not play after gear up).</param>
/// <param name="Tag">Short label for logs/events, and a semantic key for suppression rules
/// (the sterile rule exempts <c>cabin.*</c> tags).</param>
public sealed record SpeechRequest(
    string Text,
    SpeechPriority Priority = SpeechPriority.Normal,
    TimeSpan? Ttl = null,
    Func<bool>? IsStillValid = null,
    string? Tag = null);

/// <summary>Terminal fate of a submitted request — what the caller's awaited task resolves to.
/// Arbitration never throws at callers; it reports one of these instead.</summary>
public enum SpeechOutcome
{
    /// <summary>Played to completion (or synthesis produced silence — still "done").</summary>
    Spoken,

    /// <summary>Discarded: empty text, shutdown, validity predicate false, or caller cancel.</summary>
    Dropped,

    /// <summary>TTL elapsed before it could be spoken.</summary>
    Expired,

    /// <summary>A suppression rule vetoed it outright.</summary>
    Suppressed,

    /// <summary>Pre-empted by a Critical and not eligible for restart.</summary>
    Superseded,

    /// <summary>Synthesis/playback threw — logged, queue moves on.</summary>
    Failed,
}

/// <summary>Kinds of arbiter lifecycle events published for diagnostics/the event log.</summary>
public enum SpeechEventKind
{
    Enqueued,
    Dequeued,
    Spoken,
    Preempted,
    Requeued,
    Deferred,
    Dropped,
    Expired,
    Suppressed,
    Failed,
}

/// <summary>One observable arbiter event (fed to the JSONL event log and the /speech page).</summary>
public sealed record SpeechArbiterEvent(
    SpeechEventKind Kind,
    SpeechPriority Priority,
    string? Tag,
    string Text,
    string? Reason);
