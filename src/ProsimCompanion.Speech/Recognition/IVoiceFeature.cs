namespace ProsimCompanion.Speech.Recognition;

/// <summary>
/// A voice-drivable feature (FCU, radios, role handover…) plugged into the utterance router.
/// Value-parsing features receive the RAW transcription before snapping (so "descend flight
/// level one two zero" keeps its number); the rest see interpreter-resolved text. Features
/// are consulted only when no checklist item is awaiting an answer.
/// </summary>
public interface IVoiceFeature
{
    /// <summary>Grammar phrases contributed to the idle listening window.</summary>
    IEnumerable<string> Phrases { get; }

    /// <summary>True to receive the raw transcription before the snapper runs.</summary>
    bool ValueParse { get; }

    /// <summary>Handles the utterance; true = consumed.</summary>
    bool TryHandle(string utterance);
}
