namespace ProsimCompanion.Speech.Recognition;

/// <summary>
/// The last stop before the idle-miss line (issue #149): an utterance nothing routed — no
/// value parser, no command, no feature, no drill, no checklist start — is offered here with
/// its RAW transcription. The handler is read-only: whatever it does ends in speech and a
/// session event, never in an aircraft action. Contributes no grammar — it only exists for
/// the free-text LAN engine, and the router checks <see cref="IRecognitionWindow.FreeFormCapable"/>
/// before calling.
/// </summary>
public interface IFreeFormQuestionHandler
{
    /// <summary>True when the handler is switched on.</summary>
    bool Enabled { get; }

    /// <summary>Takes the utterance as a question. True = consumed (an answer, a "stand by" or
    /// a deliberate silence); false = not a question, the idle-miss line follows.</summary>
    bool TryAsk(string rawUtterance);
}
