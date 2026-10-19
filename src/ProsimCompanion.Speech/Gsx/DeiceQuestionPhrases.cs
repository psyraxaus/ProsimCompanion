using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Gsx;

/// <summary>
/// The FO's "request de-icing?" question (2026-10-09): with <c>gsx.deice.autoRequest = ask</c>
/// the GSX policy raises the question when the OAT and the departure METAR call for de-icing;
/// the shared dialogue voices it ("Captain, conditions call for de-icing — OAT −2, snow.
/// Request it?") from this table and answers the Core store — the sequencer then places or
/// skips the DeIce step. The store re-raises the question once when the de-icing step's turn
/// comes (attempt 2, issue #166); <see cref="SpokenPrompt"/> says so.
/// </summary>
public static class DeiceQuestionPhrases
{
    /// <summary>What the FO says when nobody answered — the question stands. Probed by
    /// <c>gsx-deice-policy</c>; keep the wording stable.</summary>
    public const string QuestionStandsPhrase =
        "Your call — the question stands. Say request de-icing or no de-icing when you decide, or use the Status board.";

    private static readonly IReadOnlyList<string> Grammar =
    [
        .. ConfirmVocabulary.Affirm, .. ConfirmVocabulary.Negative, .. ConfirmVocabulary.SayAgain,
        "request de-icing", "request deicing", "request de-ice", "no de-icing", "negative de-icing", "not today", "your call", "leave it", "cancel",
    ];

    /// <summary>The table the shared dialogue voices the de-icing question from.</summary>
    public static CaptainsQuestionPhrases Table { get; } = new(
        Topic: DeiceRequestStore.QuestionDefinition.Topic,
        LogName: "De-ice question",
        MicOwner: "deiceQuestion",
        EventName: "deice-question",
        Grammar: _ => Grammar,
        SpokenPrompt: SpokenPrompt,
        Interpret: (heard, _) => Interpret(heard) switch
        {
            true => QuestionReply.Yes,
            false => QuestionReply.No,
            null => null,
        },
        UnclearPrompt: _ => "Say again — request de-icing, or negative?",
        UnansweredPhrase: (_, _) => QuestionStandsPhrase,
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
            questionOpen = true,
            expiresAtUtc = state.Question?.ExpiresAtUtc,
        },
        YesReadback: "Copied — requesting de-icing.",
        NoReadback: "Copied — no de-icing.",
        // "Say again" repeats the bare prompt, not the re-ask's preamble.
        RepeatPrompt: question => question.Prompt);

    /// <summary>Classifies one heard utterance: true = request, false = decline, null = not
    /// an answer (say again / unclear). Pure, for tests.</summary>
    public static bool? Interpret(string heard)
    {
        ArgumentNullException.ThrowIfNull(heard);
        var text = CommandMatcher.Normalize(heard);
        if (text.Contains("no de", StringComparison.Ordinal) || text.Contains("not today", StringComparison.Ordinal)
            || text.Contains("negative de", StringComparison.Ordinal) || text.Contains("decline", StringComparison.Ordinal)
            || text.Contains("not required", StringComparison.Ordinal)
            || text.Contains("your call", StringComparison.Ordinal) || text.Contains("leave it", StringComparison.Ordinal)
            || text == "cancel")
        {
            return false;
        }

        if (text.Contains("request", StringComparison.Ordinal) || text.Contains("de ice", StringComparison.Ordinal)
            || text.Contains("deice", StringComparison.Ordinal) || text.Contains("de icing", StringComparison.Ordinal))
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

    /// <summary>The spoken prompt for an attempt: the re-ask when the step's turn has come
    /// says so, because the first ask may be minutes old.</summary>
    public static string SpokenPrompt(CaptainsQuestion question)
    {
        ArgumentNullException.ThrowIfNull(question);
        return question.Attempt > 1
            ? $"Captain, de-icing is the last item before pushback and still needs your call. {question.Prompt}"
            : question.Prompt;
    }
}
