using ProsimCompanion.Core.Persona;
using ProsimCompanion.Speech.Gsx;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Persona;

/// <summary>
/// The "Change First Officer now, captain — are you sure?" table (issue #194) the shared
/// <see cref="CaptainsQuestionDialogue"/> voices when "new first officer" is said mid-day. A
/// plain yes/no: yes re-draws (the voice feature acts on the store's Answered edge), no keeps
/// the crew. Silence leaves it open under the #166 rules; the 5-minute TTL counts as no.
/// </summary>
public static class FoRosterChangePhrases
{
    private static readonly IReadOnlyList<string> Grammar =
    [
        .. ConfirmVocabulary.Affirm, .. ConfirmVocabulary.Negative, .. ConfirmVocabulary.SayAgain,
        "go ahead", "do it", "change", "swap", "keep", "stay", "cancel", "leave it",
    ];

    public static CaptainsQuestionPhrases Table { get; } = new(
        Topic: FoRosterChangeStore.QuestionDefinition.Topic,
        LogName: "FO roster change question",
        MicOwner: "foRosterChange",
        EventName: "persona.rosterChangeQuestion",
        Grammar: _ => Grammar,
        SpokenPrompt: question => question.Prompt,
        Interpret: (heard, _) => Interpret(heard) switch
        {
            true => QuestionReply.Yes,
            false => QuestionReply.No,
            null => null,
        },
        UnclearPrompt: _ => "Say again, captain — change First Officer, or keep me?",
        UnansweredPhrase: (_, _) => "Your call, captain — say new first officer again when you decide.",
        RaisedPayload: question => new { prompt = question.Prompt, attempt = question.Attempt },
        AnsweredPayload: (_, reply, heard) => new { outcome = reply.Kind == QuestionReplyKind.Yes ? "change" : "keep", heard },
        UnansweredPayload: (_, asking) => new { outcome = "unanswered", open = asking.QuestionStands },
        YesReadback: null, // the feature speaks the new FO's intro instead
        NoReadback: "Copied, captain — I'm staying.");

    /// <summary>true = change, false = keep, null = not an answer. Pure, for tests.</summary>
    public static bool? Interpret(string heard)
    {
        ArgumentNullException.ThrowIfNull(heard);
        var text = CommandMatcher.Normalize(heard);
        if (text.Length == 0)
        {
            return null;
        }

        if (text is "keep" or "stay" or "cancel" || text.Contains("leave it", StringComparison.Ordinal)
            || text.Contains("keep", StringComparison.Ordinal))
        {
            return false;
        }

        if (text is "change" or "swap" || text.Contains("go ahead", StringComparison.Ordinal)
            || text.Contains("do it", StringComparison.Ordinal))
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
