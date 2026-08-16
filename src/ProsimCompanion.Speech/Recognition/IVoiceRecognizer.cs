namespace ProsimCompanion.Speech.Recognition;

/// <summary>One recognition event. Acoustic fields are null on engines that don't produce
/// them (WinRT/System.Speech) — the interpreter's acoustic gates then no-op.</summary>
public sealed class RecognizedEventArgs : EventArgs
{
    public RecognizedEventArgs(string text, double confidence, double? acousticConfidence, double? noSpeechProb)
    {
        Text = text;
        Confidence = confidence;
        AcousticConfidence = acousticConfidence;
        NoSpeechProb = noSpeechProb;
    }

    public string Text { get; }
    public double Confidence { get; }
    public double? AcousticConfidence { get; }
    public double? NoSpeechProb { get; }
}

/// <summary>
/// A continuous recognition engine with grammar windows (Prosim2FO shape): the engine listens
/// while told to, raising Accepted/Rejected per utterance. Engines transcribe ONLY — snapping,
/// acoustic gating and interpretation live in one place (<see cref="UtteranceInterpreter"/>),
/// deliberately unlike the predecessor's duplicated legacy path.
/// </summary>
public interface IVoiceRecognizer : IDisposable
{
    /// <summary>Replaces the active phrase list. <see cref="NumberGrammar.Sentinel"/> marks
    /// "a spoken number is also acceptable" for closed-grammar engines.</summary>
    void SetGrammar(IReadOnlyList<string> phrases);

    /// <summary>True while the ENGINE is actually capturing/recognizing. This is the
    /// reconcile authority for <see cref="RecognitionController"/> (issue #61): the
    /// controller used to latch its own intent, so one swallowed start failure (mic busy at
    /// app boot) left continuous listening dead until a PTT toggle happened to force a state
    /// change through the latch.</summary>
    bool IsListening { get; }

    /// <summary>Starts (or keeps) listening. Returns true when the engine is actually
    /// listening afterwards; false when the start failed (device busy/absent, engine
    /// unavailable) so the caller can retry — engines must not throw here.</summary>
    bool StartListening();

    void StopListening();

    /// <summary>An utterance was recognized (engines raise this on their own threads).</summary>
    event EventHandler<RecognizedEventArgs>? Accepted;

    /// <summary>An utterance was heard but not usable (below threshold, transcription failed).</summary>
    event EventHandler<RecognizedEventArgs>? Rejected;
}

/// <summary>Grammar sentinel: "{number}" in a phrase list means numeric input is acceptable.</summary>
public static class NumberGrammar
{
    public const string Sentinel = "{number}";
}
