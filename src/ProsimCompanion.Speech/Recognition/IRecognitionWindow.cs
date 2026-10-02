namespace ProsimCompanion.Speech.Recognition;

/// <summary>
/// The listening-window surface of <see cref="RecognitionController"/>, narrowed to what
/// consumers actually use so the utterance router and the mic-ownership seam are testable
/// against a fake, and so <see cref="MicOwnership"/> can snapshot the current window at borrow
/// time and replay it verbatim on release.
/// </summary>
public interface IRecognitionWindow
{
    /// <summary>Raised per recognized utterance (engine threads).</summary>
    event EventHandler<RecognizedEventArgs>? Accepted;

    /// <summary>Raised per unusable utterance (engine threads).</summary>
    event EventHandler<RecognizedEventArgs>? Rejected;

    /// <summary>True while the active engine returns free text (the LAN transcription engine).
    /// The offline closed-grammar engine can only return grammar phrases, so free-form
    /// consumers — the FO questions (issue #149) — stand down while it is active.</summary>
    bool FreeFormCapable { get; }

    /// <summary>True while a listening window is open.</summary>
    bool WindowOpen { get; }

    /// <summary>The most recently set grammar (survives a window close, mirroring the
    /// controller's engine-swap replay semantics).</summary>
    IReadOnlyList<string> CurrentGrammar { get; }

    void OpenListeningWindow(IReadOnlyList<string> grammar);

    void CloseListeningWindow();
}
