using ProsimCompanion.Core.Crew;
using ProsimCompanion.Speech.Gsx;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Crew;

/// <summary>
/// "I'd like to raise it in the tech log — shall I?" (issue #196) as the shared
/// <see cref="CaptainsQuestionDialogue"/> voices it after a deferrable walkaround finding. A
/// yes hands the pre-filled item to the tech-log raise path (which still reads it back and
/// confirms), a no leaves it noted. Silence leaves the question open under the #166 rules.
/// </summary>
public static class WalkaroundRaisePhrases
{
    private static readonly IReadOnlyList<string> Grammar =
    [
        .. ConfirmVocabulary.Affirm, .. ConfirmVocabulary.Negative, .. ConfirmVocabulary.SayAgain,
        "raise it", "log it", "go ahead", "enter it", "leave it", "not now", "just note it", "noted",
    ];

    public static CaptainsQuestionPhrases Table { get; } = new(
        Topic: WalkaroundRaiseStore.QuestionDefinition.Topic,
        LogName: "Walkaround raise question",
        MicOwner: "walkaroundRaise",
        EventName: "walkaround.raiseQuestion",
        Grammar: _ => Grammar,
        SpokenPrompt: question => question.Prompt,
        Interpret: (heard, _) => Interpret(heard) switch
        {
            true => QuestionReply.Yes,
            false => QuestionReply.No,
            null => null,
        },
        UnclearPrompt: _ => "Say again, captain — raise it in the tech log, or just note it?",
        UnansweredPhrase: (_, _) => "Your call, captain — it's noted; say log a defect if you want it in the tech log.",
        RaisedPayload: question => new { prompt = question.Prompt, attempt = question.Attempt },
        AnsweredPayload: (_, reply, heard) => new { outcome = reply.Kind == QuestionReplyKind.Yes ? "raise" : "noted", heard },
        UnansweredPayload: (_, asking) => new { outcome = "unanswered", open = asking.QuestionStands },
        YesReadback: null, // the raise dialogue reads the item back itself
        NoReadback: "Noted, captain.");

    /// <summary>true = raise it, false = just note it, null = not an answer. Pure, for tests.</summary>
    public static bool? Interpret(string heard)
    {
        ArgumentNullException.ThrowIfNull(heard);
        var text = CommandMatcher.Normalize(heard);
        if (text.Length == 0)
        {
            return null;
        }

        if (text.Contains("leave it", StringComparison.Ordinal) || text.Contains("not now", StringComparison.Ordinal)
            || text.Contains("note it", StringComparison.Ordinal) || text == "noted")
        {
            return false;
        }

        if (text.Contains("raise it", StringComparison.Ordinal) || text.Contains("log it", StringComparison.Ordinal)
            || text.Contains("go ahead", StringComparison.Ordinal) || text.Contains("enter it", StringComparison.Ordinal))
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
