namespace ProsimCompanion.Speech.Recognition;

/// <summary>
/// A voice-drivable feature (FCU, radios, role handover…) plugged into the utterance router.
/// Value-parsing features receive the RAW transcription before snapping (so "descend flight
/// level one two zero" keeps its number); the rest see interpreter-resolved text. Features
/// are consulted only when no checklist item is awaiting an answer.
/// </summary>
public interface IVoiceFeature
{
    /// <summary>Whether the feature is currently switched on (campaign #82). The router owns
    /// the gate: a disabled feature contributes NO phrases to the closed grammar (a smaller
    /// grammar mishears less) and is never offered the utterance — implementations no longer
    /// need to hand-roll the check in both places. Option-backed features return their live
    /// option value; always-on features return true.</summary>
    bool Enabled { get; }

    /// <summary>Grammar phrases contributed to the idle listening window (when
    /// <see cref="Enabled"/>).</summary>
    IEnumerable<string> Phrases { get; }

    /// <summary>True to receive the raw transcription before the snapper runs.</summary>
    bool ValueParse { get; }

    /// <summary>Handles the utterance; true = consumed. Not called while disabled.</summary>
    bool TryHandle(string utterance);
}
