namespace ProsimCompanion.Speech.Arbiter;

/// <summary>
/// The single facade through which all speech happens — features never call TTS or playback
/// directly. Arbitration never throws at callers: the returned task resolves to the request's
/// terminal <see cref="SpeechOutcome"/>; most call sites fire-and-forget with
/// <c>_ = arbiter.EnqueueAsync(...)</c>.
/// </summary>
public interface ISpeechArbiter
{
    /// <summary>Queues a request. Cancelling <paramref name="cancellationToken"/> withdraws
    /// this item (stopping its own playback if it is mid-utterance) and never affects others.</summary>
    Task<SpeechOutcome> EnqueueAsync(SpeechRequest request, CancellationToken cancellationToken = default);

    /// <summary>Convenience for plain speech at a given priority.</summary>
    Task<SpeechOutcome> SpeakAsync(
        string text,
        SpeechPriority priority = SpeechPriority.Normal,
        CancellationToken cancellationToken = default);

    /// <summary>Lifecycle event stream (also recorded to the JSONL event log). Fires on
    /// whichever thread produced the event; handler exceptions are swallowed.</summary>
    event Action<SpeechArbiterEvent>? Observed;
}
