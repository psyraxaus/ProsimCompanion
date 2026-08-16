namespace ProsimCompanion.Core.State;

/// <summary>Health of one TTS provider in the router chain.</summary>
public enum TtsProviderHealth
{
    /// <summary>Not configured (or excluded by local-only mode) — never attempted.</summary>
    Disabled,

    /// <summary>Configured but not yet exercised.</summary>
    Unknown,

    /// <summary>Last synthesis attempt succeeded.</summary>
    Healthy,

    /// <summary>Cooling down after a failure — skipped until the retry window elapses.</summary>
    Failed,
}

/// <summary>Point-in-time view of one TTS provider.</summary>
public sealed record TtsProviderView(string Name, TtsProviderHealth Health, string Detail);

/// <summary>One utterance as the arbiter disposed of it (spoken, dropped or deferred).</summary>
public sealed record UtteranceView(
    DateTimeOffset TimestampUtc,
    string Text,
    string Priority,
    string Source,
    string Outcome);

/// <summary>Everything the web Speech page renders.</summary>
public sealed record SpeechStatusSnapshot(
    bool Enabled,
    bool SterileCockpit,
    int QueueDepth,
    string? NowPlaying,
    IReadOnlyList<TtsProviderView> Providers,
    IReadOnlyList<UtteranceView> RecentUtterances,
    bool Listening = false,
    string LastHeard = "",
    string SpokenChecklist = "",
    string SpokenChecklistItem = "",
    bool CabinCalling = false,
    string ActiveAbnormal = "")
{
    public static SpeechStatusSnapshot Empty { get; } = new(
        Enabled: false,
        SterileCockpit: false,
        QueueDepth: 0,
        NowPlaying: null,
        Providers: [],
        RecentUtterances: []);
}

/// <summary>
/// Live status of the speech pillar. Kept in Core so the Web project (which references only
/// Core) can render it. Written by ProsimCompanion.Speech.
/// </summary>
public sealed class SpeechStatusStore : SnapshotStore<SpeechStatusSnapshot>
{
    public SpeechStatusStore()
        : base(SpeechStatusSnapshot.Empty)
    {
    }
}

/// <summary>Commands the web UI can issue to the speech pillar (implemented by
/// ProsimCompanion.Speech; registered only when the pillar is).</summary>
public interface ISpeechControl
{
    /// <summary>Speaks a test phrase through the full arbiter/router/playback path at Normal
    /// priority — the /speech page's "say something" button.</summary>
    void SpeakTest(string text);
}

/// <summary>Web-side escape hatch for the interactive ECAM abnormal dialogue (issue #56:
/// voice must never be the ONLY way out of a dialogue that holds the mic). Implemented by
/// the speech pillar's FailureMonitor; the running dialogue's title is on
/// <see cref="SpeechStatusSnapshot.ActiveAbnormal"/>.</summary>
public interface IAbnormalDialogueControl
{
    /// <summary>Cancels the running ECAM dialogue, if any; returns whether one was active.
    /// The FO acknowledges the cancellation aloud and normal voice routing resumes.</summary>
    bool CancelActiveDialogue();
}
