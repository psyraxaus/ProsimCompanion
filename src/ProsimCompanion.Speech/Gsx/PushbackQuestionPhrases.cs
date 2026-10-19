using ProsimCompanion.Core.Airports.Parking;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Gsx;

/// <summary>
/// The FO's "which way?" question (2026-10-04): when GSX's direction menu is open and the
/// automation has no choice and no confident suggestion (mode auto + ask-when-unsure, or
/// mode ask), the <see cref="PushbackChoiceStore"/> raises a question and the shared dialogue
/// voices it from this table — "Pushback: tail left or tail right?" (with the stand's own
/// labels when the profile names them) — and hands the pilot's answer to the pushback voice
/// feature, which stores it and reads it back. The interpretation adapter here is the
/// pushback's own odd shape: "push back facing north" is a compass wish, not one of GSX's menu
/// lines, so the table settles it itself through <see cref="PushbackDirectionVoiceFeature.Apply"/>.
/// Timeout or two misses hand the menu back to the pilot with a word; the question stays
/// open until the menu closes or the store's TTL withdraws it (C2).
/// </summary>
public static class PushbackQuestionPhrases
{
    private static readonly IReadOnlyList<string> Grammar =
        [.. PushbackPhraseParser.Phrases, "left", "right", "straight", .. ConfirmVocabulary.SayAgain, "your call", "leave it", "cancel"];

    /// <summary>The table the shared dialogue voices the pushback question from; the feature
    /// applies and reads back the pilot's wish.</summary>
    public static CaptainsQuestionPhrases Create(PushbackDirectionVoiceFeature feature)
    {
        ArgumentNullException.ThrowIfNull(feature);
        return new CaptainsQuestionPhrases(
            Topic: PushbackChoiceStore.QuestionDefinition.Topic,
            LogName: "Pushback question",
            MicOwner: "pushbackQuestion",
            EventName: "pushback-question",
            Grammar: _ => Grammar,
            SpokenPrompt: question => question.Prompt,
            Interpret: (heard, _) =>
            {
                var text = CommandMatcher.Normalize(heard);
                if (text.Contains("your call", StringComparison.Ordinal) || text.Contains("leave it", StringComparison.Ordinal) || text == "cancel")
                {
                    return QuestionReply.HandBack;
                }

                var choice = PushbackPhraseParser.Parse(heard)
                    ?? (text == "left" ? PushbackChoice.TailLeft("voice", $"pilot said '{heard}'")
                        : text == "right" ? PushbackChoice.TailRight("voice", $"pilot said '{heard}'")
                        : null);
                return choice is null ? null : QuestionReply.Settled(() => feature.Apply(choice));
            },
            UnclearPrompt: _ => "Say again — tail left, tail right, or facing which way?",
            UnansweredPhrase: (_, _) => "Your call — the GSX pushback menu is open.",
            RaisedPayload: question => new { prompt = question.Prompt, options = question.Choices.Select(c => c.Label).ToList() },
            // The feature records its own pushback-choice event on Apply.
            AnsweredPayload: (_, _, _) => null,
            UnansweredPayload: (_, _) => new { outcome = "unanswered" });
    }
}
