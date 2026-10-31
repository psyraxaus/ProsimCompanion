using ProsimCompanion.Core.Scenarios;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Gsx;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Scenarios;

/// <summary>
/// The FO's decision turn after a scenario's ECAM actions (issue #197) as the shared
/// <see cref="CaptainsQuestionDialogue"/> voices it: the composed line is the prompt, the
/// answer is one of continue / return / divert. Silence leaves it open under the #166 rules;
/// the TTL settles it as continue (nothing changes).
/// </summary>
public static class ScenarioDecisionPhrases
{
    private static readonly IReadOnlyList<string> Grammar =
    [
        .. ConfirmVocabulary.Affirm, .. ConfirmVocabulary.Negative, .. ConfirmVocabulary.SayAgain,
        "continue", "we continue", "carry on", "press on", "return", "we return", "go back", "turn back",
        "divert", "we divert", "divert to the alternate", "your call", "stand by",
    ];

    public static CaptainsQuestionPhrases Table { get; } = new(
        Topic: ScenarioDecisionStore.QuestionDefinition.Topic,
        LogName: "Scenario decision",
        MicOwner: "scenarioDecision",
        EventName: "scenario.decisionQuestion",
        Grammar: _ => Grammar,
        SpokenPrompt: question => question.Prompt,
        Interpret: (heard, question) => Interpret(heard, question.Choices) is { } choice ? QuestionReply.Pick(choice) : null,
        UnclearPrompt: _ => "Say again, captain — continue, return, or divert?",
        UnansweredPhrase: (_, _) => "Your call, captain — we continue unless you say return or divert.",
        RaisedPayload: question => new { prompt = question.Prompt, scenario = question.Subject, attempt = question.Attempt },
        AnsweredPayload: (question, reply, heard) => new { outcome = reply.Choice?.Label, scenario = question.Subject, heard },
        UnansweredPayload: (question, asking) => new { outcome = "unanswered", scenario = question.Subject, open = asking.QuestionStands },
        PickReadback: choice => choice.Label switch
        {
            ScenarioDecisionStore.Return => "Returning — I'll set it up.",
            ScenarioDecisionStore.Divert => "Diverting to the alternate — I'll set it up.",
            _ => "Continuing, captain.",
        });

    /// <summary>continue / return / divert from the captain's words; a bare yes means the FO's
    /// suggestion (the first choice, continue), a bare no means not continue → the FO asks
    /// again. Pure, for tests.</summary>
    public static QuestionChoice? Interpret(string heard, IReadOnlyList<QuestionChoice> choices)
    {
        ArgumentNullException.ThrowIfNull(heard);
        ArgumentNullException.ThrowIfNull(choices);
        var text = CommandMatcher.Normalize(heard);
        if (text.Length == 0)
        {
            return null;
        }

        if (text.Contains("divert", StringComparison.Ordinal))
        {
            return Find(choices, ScenarioDecisionStore.Divert);
        }

        if (text.Contains("return", StringComparison.Ordinal) || text.Contains("go back", StringComparison.Ordinal) || text.Contains("turn back", StringComparison.Ordinal))
        {
            return Find(choices, ScenarioDecisionStore.Return);
        }

        if (text.Contains("continue", StringComparison.Ordinal) || text.Contains("carry on", StringComparison.Ordinal) || text.Contains("press on", StringComparison.Ordinal)
            || ConfirmVocabulary.Affirm.Any(a => text == a || text.StartsWith(a + " ", StringComparison.Ordinal)))
        {
            return Find(choices, ScenarioDecisionStore.Continue);
        }

        return null;
    }

    private static QuestionChoice? Find(IReadOnlyList<QuestionChoice> choices, string label)
        => choices.FirstOrDefault(c => string.Equals(c.Label, label, StringComparison.OrdinalIgnoreCase));
}
