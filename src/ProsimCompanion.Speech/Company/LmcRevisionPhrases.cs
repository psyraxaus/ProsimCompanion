using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Gsx;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Company;

/// <summary>
/// The FO's "boarding differs from the final loadsheet by n passengers — issue a revised
/// final?" (issue #181), voiced by the shared <see cref="CaptainsQuestionDialogue"/> from this
/// table. Yes → the loadsheet pipeline re-runs the final with EDNO +1; no → the LMC block
/// stands with the "flown on captain's authority" caption. Silence leaves the question open
/// (#166); the 5-minute TTL counts as no.
/// </summary>
public static class LmcRevisionPhrases
{
    private static readonly IReadOnlyList<string> Grammar =
    [
        .. ConfirmVocabulary.Affirm, .. ConfirmVocabulary.Negative, .. ConfirmVocabulary.SayAgain,
        "issue it", "issue the revised", "revised final", "revised loadsheet", "new loadsheet", "send it",
        "my authority", "captain's authority", "on my authority", "no new loadsheet", "we fly", "leave it", "your call",
    ];

    public static CaptainsQuestionPhrases Table { get; } = new(
        Topic: LmcRevisionStore.QuestionDefinition.Topic,
        LogName: "Revised-final question",
        MicOwner: "loadsheetRevision",
        EventName: "loadsheet-revision-question",
        Grammar: _ => Grammar,
        SpokenPrompt: question => question.Prompt,
        Interpret: (heard, _) => Interpret(heard) switch
        {
            true => QuestionReply.Yes,
            false => QuestionReply.No,
            null => null,
        },
        UnclearPrompt: _ => "Say again — issue a revised final, or fly on your authority?",
        UnansweredPhrase: (_, _) => "Your call — say revised final or on my authority when you decide, or use the Loadsheet page.",
        RaisedPayload: question => new { prompt = question.Prompt, attempt = question.Attempt },
        AnsweredPayload: (question, reply, heard) => new
        {
            outcome = reply.Kind == QuestionReplyKind.Yes ? "revise" : "captains-authority",
            heard,
            attempt = question.Attempt,
        },
        UnansweredPayload: (question, state) => new
        {
            outcome = "unanswered",
            attempt = question.Attempt,
            expiresAtUtc = state.Question?.ExpiresAtUtc,
        },
        YesReadback: "Copied — requesting a revised final loadsheet.",
        NoReadback: "Copied — flying on your authority, the last minute change stands.");

    /// <summary>true = issue the revision, false = fly on the captain's authority, null = not
    /// an answer. Pure, for tests.</summary>
    public static bool? Interpret(string heard)
    {
        ArgumentNullException.ThrowIfNull(heard);
        var text = CommandMatcher.Normalize(heard);
        if (text.Contains("authority", StringComparison.Ordinal) || text.Contains("no new loadsheet", StringComparison.Ordinal)
            || text.Contains("we fly", StringComparison.Ordinal) || text.Contains("leave it", StringComparison.Ordinal))
        {
            return false;
        }

        if (text.Contains("revised", StringComparison.Ordinal) || text.Contains("new loadsheet", StringComparison.Ordinal)
            || text.Contains("issue", StringComparison.Ordinal) || text.Contains("send it", StringComparison.Ordinal))
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
