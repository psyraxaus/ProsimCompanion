using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Speech.Recognition;

/// <summary>What the interpreter decided.</summary>
public enum InterpretKind
{
    /// <summary>Fire <see cref="InterpretResult.Text"/> (a vocabulary phrase, or raw text
    /// passed through to an awaiting item's answer logic).</summary>
    Resolved,

    /// <summary>Gray band — ask "did you mean {Text}?" before firing.</summary>
    Confirm,

    /// <summary>Unusable — "say again".</summary>
    Reject,

    /// <summary>A known ASR silence/breath hallucination ("Thank you.", issue #120) —
    /// absorbed silently, never a spoken reject. Only outside answer windows: a phrase on
    /// the list can still be a valid checklist answer, and answers are consumed first.</summary>
    Hallucination,
}

/// <summary>Interpretation context: whether a checklist item is awaiting an answer, plus the
/// engine's acoustic fields (null on offline engines — gates then no-op).</summary>
public sealed record InterpretContext(bool ItemAwaiting, double? AcousticConfidence, double? NoSpeechProb);

public sealed record InterpretResult(InterpretKind Kind, string Text, double Score);

/// <summary>
/// The single interpretation path (Prosim2FO's ladder, with the legacy duplicate deliberately
/// dropped): acoustic gates (command windows only — an awaiting readback is never gated out,
/// the dataref backstop covers it) → exact normalized match → phonetic snap with a gray
/// confirmation band → raw pass-through for awaiting items → reject.
/// </summary>
public sealed class UtteranceInterpreter
{
    private readonly IOptionsMonitor<SpeechOptions> _options;

    public UtteranceInterpreter(IOptionsMonitor<SpeechOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public InterpretResult Interpret(string rawText, IReadOnlyList<string> vocabulary, InterpretContext context)
    {
        ArgumentNullException.ThrowIfNull(vocabulary);
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(rawText) || vocabulary.Count == 0)
        {
            return new InterpretResult(InterpretKind.Reject, "", 0);
        }

        var options = _options.CurrentValue;

        if (!context.ItemAwaiting)
        {
            if (context.NoSpeechProb is { } nsp && nsp >= options.NoSpeechCeiling)
            {
                return new InterpretResult(InterpretKind.Reject, "", 0);
            }

            if (options.ConfidenceFloor > 0
                && context.AcousticConfidence is { } conf && conf < options.ConfidenceFloor)
            {
                return new InterpretResult(InterpretKind.Reject, "", 0);
            }
        }

        // Whisper hallucination filter (issue #120, 2026-08-29 flight: 8× "Thank you." from
        // breaths/silence, each one an audible FO reject in cruise). Checked BEFORE snapping
        // — "thank you" fuzzy-matched nothing anyway, but must never reach the reject chirp.
        // Skipped while an item awaits an answer: those windows consume their own words.
        var normalizedEarly = CommandMatcher.Normalize(rawText);
        if (!context.ItemAwaiting
            && options.AsrHallucinationPhrases.Any(h =>
                CommandMatcher.Normalize(h).Equals(normalizedEarly, StringComparison.Ordinal)))
        {
            return new InterpretResult(InterpretKind.Hallucination, rawText, 0);
        }

        // Exact match accepts the spoken-digit form too ("flaps 1" == "flaps one", #119):
        // whisper chooses the written form freely and the pilot said the same thing.
        var normalized = normalizedEarly;
        var digitsSpoken = CommandMatcher.SpeakSingleDigits(normalized);
        foreach (var phrase in vocabulary)
        {
            if (phrase != NumberGrammar.Sentinel
                && CommandMatcher.Normalize(phrase) is var phraseNorm
                && (phraseNorm.Equals(normalized, StringComparison.Ordinal)
                    || phraseNorm.Equals(digitsSpoken, StringComparison.Ordinal)))
            {
                return new InterpretResult(InterpretKind.Resolved, phrase, 1.0);
            }
        }

        var snap = CommandMatcher.Snap(rawText,
            [.. vocabulary.Where(v => v != NumberGrammar.Sentinel)], options.SnappingThreshold);
        if (snap is not null)
        {
            if (!context.ItemAwaiting && snap.Score < options.ConfirmBelowScore)
            {
                return new InterpretResult(InterpretKind.Confirm, snap.Command, snap.Score);
            }

            return new InterpretResult(InterpretKind.Resolved, snap.Command, snap.Score);
        }

        if (context.ItemAwaiting)
        {
            // Pass the raw readback through to the item's answer logic instead of snapping
            // it away — number/whole-word matching happens there.
            return new InterpretResult(InterpretKind.Resolved, rawText, context.AcousticConfidence ?? 0.0);
        }

        return new InterpretResult(InterpretKind.Reject, rawText, 0);
    }
}
