namespace ProsimCompanion.Speech.Recognition;

/// <summary>
/// Exclusive-microphone seam for guided voice dialogues (tech-log raise/rectify, the
/// post-abnormal shutdown offer). While borrowed, normal utterance routing — feature dispatch
/// and checklist answers — ignores recognition results, so a running spoken checklist simply
/// holds: its pending answer stays pending and it resumes when the borrow ends, because
/// disposal replays the exact window state (grammar plus open/closed) captured at borrow time.
/// Disposal restores ALWAYS, including on exception paths; callers hold the borrow in a
/// <c>using</c>.
/// </summary>
public interface IMicOwnership
{
    /// <summary>True while a dialogue owns the microphone (routing must ignore results).</summary>
    bool IsBorrowed { get; }

    /// <summary>Raised AFTER a borrow was released and the pre-borrow window replayed, on the
    /// releaser's thread. Consumers that own a standing window (the checklist engine's idle
    /// grammar) re-assert it here: the replay restores the state captured at BORROW time, so
    /// a dialogue that borrowed before the engine opened its window would otherwise close
    /// that window forever on release (issue #61's startup-ordering hazard).</summary>
    event Action? Released;

    /// <summary>Takes exclusive ownership. Throws <see cref="InvalidOperationException"/> when
    /// already borrowed — dialogue runners serialize themselves, so an overlap is a bug to
    /// surface, not a queue to wait in. Dispose to re-enable routing and restore the
    /// pre-borrow listening window.</summary>
    IDisposable Borrow(string owner);

    /// <summary>Opens a listening window with <paramref name="grammar"/> and resolves with the
    /// first recognized utterance (trimmed); null on timeout. An EMPTY grammar listens
    /// free-form: the raw transcription reaches the caller untouched — no interpreter, no
    /// command snapping (the defect-title capture depends on this). Free-form needs the LAN
    /// transcription engine; the closed-grammar offline engine cannot listen without phrases
    /// and times out to null.</summary>
    Task<string?> ListenAsync(IReadOnlyList<string> grammar, TimeSpan timeout, CancellationToken cancellationToken);
}
