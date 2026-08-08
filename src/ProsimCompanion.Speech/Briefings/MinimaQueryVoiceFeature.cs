using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Briefings;

/// <summary>
/// The minima recall query ("what are our minimums", "say minimums") — Prosim2FO semantics:
/// it answers ONLY from the crew-confirmed <see cref="ArrivalMinimaStore"/>, and says
/// "Minimums not briefed." when nothing has been entered — minima are never guessed
/// (there is no DH/MDA dataref; see the store's contract). A thin dispatch shim like
/// <see cref="MissedApproachVoiceFeature"/> — capture/entry lives elsewhere.
/// </summary>
public sealed class MinimaQueryVoiceFeature : IVoiceFeature
{
    private static readonly string[] QueryPhrases =
        ["what are our minimums", "what's our minimums", "say minimums", "minimums check"];

    // "what's" normalizes to "what s" — match against the normalized forms, but contribute
    // the natural phrases to the grammar.
    private static readonly string[] NormalizedPhrases =
        [.. QueryPhrases.Select(CommandMatcher.Normalize)];

    private readonly ArrivalMinimaStore _minima;
    private readonly ISpeechArbiter _arbiter;

    public MinimaQueryVoiceFeature(ArrivalMinimaStore minima, ISpeechArbiter arbiter)
    {
        ArgumentNullException.ThrowIfNull(minima);
        ArgumentNullException.ThrowIfNull(arbiter);

        _minima = minima;
        _arbiter = arbiter;
    }

    public IEnumerable<string> Phrases => QueryPhrases;

    public bool ValueParse => false;

    public bool TryHandle(string utterance)
    {
        if (!NormalizedPhrases.Contains(CommandMatcher.Normalize(utterance)))
        {
            return false;
        }

        _ = _arbiter.EnqueueAsync(new SpeechRequest(
            ComposeAnswer(_minima.Current), SpeechPriority.Normal, Tag: "briefing.minima"));
        return true;
    }

    /// <summary>Pure answer text — the briefing template's minima clause when set, else the
    /// predecessor's exact "not briefed" line. Static for tests.</summary>
    public static string ComposeAnswer(ArrivalMinima? minima)
        => minima is { } m
            ? $"Minimums, {BriefingComposer.MinimaCallout(m)}."
            : "Minimums not briefed.";
}
