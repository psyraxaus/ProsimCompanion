using ProsimCompanion.Core.Company;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Gsx;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Company;

/// <summary>
/// The FO's "we're 22 minutes late off blocks — delay code?" (issue #185), voiced by the shared
/// <see cref="CaptainsQuestionDialogue"/>. The captain answers with the number ("delay code 15",
/// "one five", "fifteen") or the words ("delay boarding", "late passengers"); the table matches
/// against the question's choices (the catalogue as loaded) and picks. "Your call" hands it
/// back; the TTL falls to 99.
/// </summary>
public static class DelayCodePhrases
{
    private static readonly IReadOnlyList<string> HandBack = ["your call", "leave it", "skip it", "no code", "cancel"];

    public static CaptainsQuestionPhrases Create(DelayCodeCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return new(
            Topic: DelayCodeStore.QuestionDefinition.Topic,
            LogName: "Delay code question",
            MicOwner: "delayCode",
            EventName: "delay-code-question",
            Grammar: question => Grammar(question, catalog),
            SpokenPrompt: question => question.Prompt,
            Interpret: (heard, question) =>
            {
                var text = CommandMatcher.Normalize(heard);
                if (HandBack.Any(h => text.Contains(h, StringComparison.Ordinal)))
                {
                    return QuestionReply.HandBack;
                }

                return Pick(heard, question.Choices, catalog.Entries) is { } choice ? QuestionReply.Pick(choice) : null;
            },
            UnclearPrompt: _ => "Say again — the delay code number, or the reason, like delay boarding?",
            UnansweredPhrase: (_, _) => "Your call — say delay code and the number when you have it; I'll file 99 otherwise.",
            RaisedPayload: question => new { prompt = question.Prompt, attempt = question.Attempt, options = question.Choices.Select(c => c.Token).ToList() },
            AnsweredPayload: (_, reply, heard) => new { outcome = "answered", code = reply.Choice?.Token, heard },
            UnansweredPayload: (_, _) => new { outcome = "unanswered", fallback = DelayCodeCatalog.OtherCode },
            PickReadback: choice => $"Delay code {string.Join(" ", choice.Token.ToCharArray())}, {choice.Detail ?? choice.Label}.");
    }

    /// <summary>The choice the words name: by number (two digits anywhere, digit words
    /// included) or by a catalogue alias. Pure, for tests.</summary>
    public static QuestionChoice? Pick(string heard, IReadOnlyList<QuestionChoice> choices, IReadOnlyList<DelayCodeEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(heard);
        ArgumentNullException.ThrowIfNull(choices);
        ArgumentNullException.ThrowIfNull(entries);
        var text = CommandMatcher.Normalize(heard);
        var withDigits = Llm.SpokenNumberText.ToDigits(text, includeSingles: true);
        var match = DelayCodeCatalog.Match(withDigits, entries) ?? DelayCodeCatalog.Match(text, entries);
        return match is null ? null : choices.FirstOrDefault(c => c.Token == match.Code);
    }

    private static IReadOnlyList<string> Grammar(CaptainsQuestion question, DelayCodeCatalog catalog)
        =>
        [
            .. ConfirmVocabulary.SayAgain, .. HandBack,
            .. NumberExtractor.GrammarWords,
            "delay code", "delay",
            .. question.Choices.Select(c => c.Token),
            .. catalog.Entries.SelectMany(e => e.Spoken),
        ];
}
