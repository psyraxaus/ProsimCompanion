namespace ProsimCompanion.Core.State;

/// <summary>The pilot's side of the voice pipeline as the diagnostics VOICE page draws it:
/// what the microphone hears, what the voice-activity gate decides, and the last utterance
/// with what the router made of it. <see cref="SpeechStatusSnapshot"/> is the First Officer's
/// side (what was said back); the two are kept apart so the mic meter's four updates a
/// second never re-render the pages that watch the arbiter.</summary>
/// <param name="MicLevelDb">Peak level of the last capture buffer in dBFS; null while not capturing.</param>
/// <param name="SampleRateHz">Capture sample rate; null while not capturing.</param>
/// <param name="InSpeech">True while the gate holds an utterance open.</param>
/// <param name="VadEngine">"silero" or "rms"; empty before the first capture.</param>
/// <param name="LastText">The last accepted transcript.</param>
/// <param name="LastConfidence">Its confidence (0..1) when the server gave one.</param>
/// <param name="LastDurationMs">The utterance length the gate measured.</param>
/// <param name="LastAtUtc">When it was accepted.</param>
/// <param name="LastIntent">The router's interpretation kind for it ("Command", "Reject"...).</param>
/// <param name="LastAction">What the router matched it to (the phrase, the feature).</param>
/// <param name="RejectsLastMinute">Utterances the recognizer rejected in the last 60 s (empty,
/// server failure, self-echo): the VOICE page's BLEED figure.</param>
public sealed record VoiceSignalSnapshot(
    double? MicLevelDb,
    int? SampleRateHz,
    bool InSpeech,
    string VadEngine,
    string LastText,
    double? LastConfidence,
    int? LastDurationMs,
    DateTimeOffset? LastAtUtc,
    string LastIntent,
    string LastAction,
    int RejectsLastMinute)
{
    public static VoiceSignalSnapshot Empty { get; } = new(null, null, false, "", "", null, null, null, "", "", 0);
}

/// <summary>Store for <see cref="VoiceSignalSnapshot"/>. Writers: the LAN recognizer (mic,
/// gate, transcript, rejects) and the utterance router (intent, action).</summary>
public sealed class VoiceSignalStore : SnapshotStore<VoiceSignalSnapshot>
{
    private readonly object _rejectGate = new();
    private readonly Queue<DateTimeOffset> _rejects = new();

    public VoiceSignalStore()
        : base(VoiceSignalSnapshot.Empty)
    {
    }

    /// <summary>The mic meter and gate state; call at most a few times a second.</summary>
    public void Signal(double? micLevelDb, int? sampleRateHz, bool inSpeech, string? vadEngine)
        => Update(s => s with
        {
            MicLevelDb = micLevelDb is null ? null : Math.Round(micLevelDb.Value, 0),
            SampleRateHz = sampleRateHz,
            InSpeech = inSpeech,
            VadEngine = vadEngine ?? s.VadEngine,
        });

    /// <summary>Capture stopped: no level, no rate, gate closed.</summary>
    public void CaptureStopped()
        => Update(s => s with { MicLevelDb = null, SampleRateHz = null, InSpeech = false });

    /// <summary>The length of the utterance the gate just closed (known before the transcript).</summary>
    public void UtteranceClosed(int durationMs)
        => Update(s => s with { LastDurationMs = durationMs });

    /// <summary>An accepted transcript; the router fills intent and action next.</summary>
    public void Accepted(string text, double? confidence, DateTimeOffset now)
        => Update(s => s with
        {
            LastText = text,
            LastConfidence = confidence,
            LastAtUtc = now,
            LastIntent = "",
            LastAction = "",
        });

    /// <summary>What the router decided for the last accepted transcript.</summary>
    public void Decided(string intent, string action)
        => Update(s => s with { LastIntent = intent ?? "", LastAction = action ?? "" });

    /// <summary>One rejected utterance; the per-minute count is derived on the next publish.</summary>
    public void Rejected(DateTimeOffset now)
    {
        int count;
        lock (_rejectGate)
        {
            _rejects.Enqueue(now);
            count = Prune(now);
        }

        Update(s => s with { RejectsLastMinute = count });
    }

    /// <summary>Re-counts the rejects inside the last minute as of <paramref name="now"/>
    /// (the collector calls this each tick so the figure decays without new rejects).</summary>
    public int RejectsWithin(DateTimeOffset now)
    {
        lock (_rejectGate)
        {
            return Prune(now);
        }
    }

    private int Prune(DateTimeOffset now)
    {
        while (_rejects.Count > 0 && now - _rejects.Peek() > TimeSpan.FromMinutes(1))
        {
            _rejects.Dequeue();
        }

        return _rejects.Count;
    }
}
