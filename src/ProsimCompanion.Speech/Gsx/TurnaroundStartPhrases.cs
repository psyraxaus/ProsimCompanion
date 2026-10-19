using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Gsx;

/// <summary>
/// The FO's "Next plan loaded — commence ground services?" question (issue #169): with
/// <c>gsx.groundPrepActivation = voiceThenAuto</c> and <c>gsx.turnaroundAskFirst</c> on, the
/// GSX automation raises the question once the next leg's OFP has settled at the stand; the
/// shared dialogue voices it from this table and answers the Core store — yes starts the
/// departure sequence as a voice start, no leaves the voice gate exactly as it was, so
/// "commence ground services" and the Start button still work. Nobody answering leaves the
/// question open (the store's TTL declines it — #166 rule, shared since C2).
/// </summary>
public static class TurnaroundStartPhrases
{
    private static readonly IReadOnlyList<string> Grammar =
    [
        .. ConfirmVocabulary.Affirm, .. ConfirmVocabulary.Negative, .. ConfirmVocabulary.SayAgain,
        .. GsxVoicePhrases.StartGroundServicesPhrases,
        "go ahead", "commence", "standby", "stand by", "not yet", "hold", "wait", "cancel",
    ];

    /// <summary>The table the shared dialogue voices the turnaround question from.</summary>
    public static CaptainsQuestionPhrases Table { get; } = new(
        Topic: TurnaroundStartStore.QuestionDefinition.Topic,
        LogName: "Turnaround start question",
        MicOwner: "turnaroundStart",
        EventName: "turnaround-start-question",
        Grammar: _ => Grammar,
        SpokenPrompt: question => question.Prompt,
        Interpret: (heard, _) => Interpret(heard) switch
        {
            true => QuestionReply.Yes,
            false => QuestionReply.No,
            null => null,
        },
        UnclearPrompt: _ => "Say again — commence ground services, or stand by?",
        UnansweredPhrase: (_, _) => "Your call — say commence ground services when you are ready.",
        RaisedPayload: question => new { prompt = question.Prompt },
        AnsweredPayload: (_, reply, heard) => new { outcome = reply.Kind == QuestionReplyKind.Yes ? "accepted" : "declined", heard },
        UnansweredPayload: (_, _) => new { outcome = "unanswered" },
        YesReadback: "Copied — commencing ground services.",
        NoReadback: "Copied — standing by for your call.");

    /// <summary>Classifies one heard utterance: true = commence, false = hold, null = not an
    /// answer (say again / unclear). Pure, for tests.</summary>
    public static bool? Interpret(string heard)
    {
        ArgumentNullException.ThrowIfNull(heard);
        var text = CommandMatcher.Normalize(heard);
        if (text.Contains("not yet", StringComparison.Ordinal) || text.Contains("stand by", StringComparison.Ordinal)
            || text.Contains("standby", StringComparison.Ordinal) || text == "hold" || text == "wait" || text == "cancel")
        {
            return false;
        }

        if (text.Contains("ground services", StringComparison.Ordinal) || text.Contains("go ahead", StringComparison.Ordinal)
            || text.Contains("commence", StringComparison.Ordinal))
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
