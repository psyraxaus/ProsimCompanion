using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Gsx;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Monitoring;

/// <summary>
/// The FO's "… recommend 20 minutes holding, 700 kilos. Take it?" (issue #183), voiced by the
/// shared <see cref="CaptainsQuestionDialogue"/> from this table. Yes → the fuel-decision
/// service raises the INIT block fuel; no → the plan stands. Silence leaves the question open
/// (#166); the TTL counts as no.
/// </summary>
public static class FuelDecisionPhrases
{
    private static readonly IReadOnlyList<string> Grammar =
    [
        .. ConfirmVocabulary.Affirm, .. ConfirmVocabulary.Negative, .. ConfirmVocabulary.SayAgain,
        "take it", "we'll take it", "take the fuel", "add it", "load it", "no extra", "plan fuel", "leave it", "your call",
    ];

    public static CaptainsQuestionPhrases Table { get; } = new(
        Topic: FuelDecisionStore.QuestionDefinition.Topic,
        LogName: "Fuel decision question",
        MicOwner: "fuelDecision",
        EventName: "fuel-decision-question",
        Grammar: _ => Grammar,
        SpokenPrompt: question => question.Prompt,
        Interpret: (heard, _) => Interpret(heard) switch
        {
            true => QuestionReply.Yes,
            false => QuestionReply.No,
            null => null,
        },
        UnclearPrompt: _ => "Say again — take the extra fuel, or keep the plan fuel?",
        UnansweredPhrase: (_, _) => "Your call — say take it, or extra fuel and a figure, before we call the truck.",
        RaisedPayload: question => new { prompt = question.Prompt, attempt = question.Attempt },
        AnsweredPayload: (question, reply, heard) => new
        {
            outcome = reply.Kind == QuestionReplyKind.Yes ? "accepted" : "declined",
            heard,
            attempt = question.Attempt,
        },
        UnansweredPayload: (question, state) => new
        {
            outcome = "unanswered",
            attempt = question.Attempt,
            expiresAtUtc = state.Question?.ExpiresAtUtc,
        },
        YesReadback: "Copied — adding the holding fuel to the block.",
        NoReadback: "Copied — plan fuel stands.");

    /// <summary>true = take it, false = plan fuel, null = not an answer. Pure, for tests.</summary>
    public static bool? Interpret(string heard)
    {
        ArgumentNullException.ThrowIfNull(heard);
        var text = CommandMatcher.Normalize(heard);
        if (text.Contains("no extra", StringComparison.Ordinal) || text.Contains("plan fuel", StringComparison.Ordinal) || text.Contains("leave it", StringComparison.Ordinal))
        {
            return false;
        }

        if (text.Contains("take", StringComparison.Ordinal) || text.Contains("add it", StringComparison.Ordinal) || text.Contains("load it", StringComparison.Ordinal))
        {
            return true;
        }

        if (ConfirmVocabulary.Negative.Any(n => text == n || text.StartsWith(n + " ", StringComparison.Ordinal)))
        {
            return false;
        }

        if (ConfirmVocabulary.Affirm.Any(a => text == a || text.StartsWith(a + " ", StringComparison.Ordinal)))
        {
            return true;
        }

        return null;
    }
}
