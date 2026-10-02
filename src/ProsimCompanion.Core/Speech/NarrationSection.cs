namespace ProsimCompanion.Core.Speech;

/// <summary>
/// One clause of a deterministic spoken template (briefing, debrief) together with what makes
/// it recognisable in someone else's words (issue #147). When an LLM-styled narration is cut
/// short mid-stream, the template finishes it — and it must say only what the pilot has not
/// heard yet. A section counts as already said when the spoken text holds ALL of its
/// <see cref="Numbers"/>, at least one of its <see cref="AnyOf"/> phrases (when it has any)
/// and its <see cref="Runway"/> (when it has one). A section with none of the three can never
/// be recognised and is always spoken: the failure direction is a short fact said twice,
/// never a fact lost.
/// </summary>
/// <param name="Key">Stable name for logs and the session event ("wind", "qnh").</param>
/// <param name="Text">The template's own sentence, exactly as it is spoken.</param>
public sealed record NarrationSection(string Key, string Text)
{
    /// <summary>The section's figures; every one must have been spoken.</summary>
    public IReadOnlyList<double> Numbers { get; init; } = [];

    /// <summary>Words or names of which at least one must have been spoken. Compared with
    /// case, spaces and punctuation ignored, so "I L S" matches "ILS".</summary>
    public IReadOnlyList<string> AnyOf { get; init; } = [];

    /// <summary>A runway designator ("16R", "04L", "27") that must have been spoken.</summary>
    public string? Runway { get; init; }

    /// <summary>The opening line ("Departure briefing."): said only when nothing was spoken
    /// before the template took over.</summary>
    public bool IsOpening { get; init; }

    /// <summary>The closing line ("Good flight."): always said when the template finishes.</summary>
    public bool IsClosing { get; init; }
}
