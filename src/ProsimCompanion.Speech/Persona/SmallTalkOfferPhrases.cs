using ProsimCompanion.Core.Persona;
using ProsimCompanion.Speech.Gsx;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Persona;

/// <summary>
/// The "Coffee, captain?" table (issue #195) the shared <see cref="CaptainsQuestionDialogue"/>
/// voices: the prompt is the composed offer line the store was raised with, a yes is the
/// captain accepting (the purser then sends something up), a no is "not now". Silence leaves
/// it open under the #166 rules; the 5-minute TTL lets the offer lapse.
/// </summary>
public static class SmallTalkOfferPhrases
{
    private static readonly IReadOnlyList<string> Grammar =
    [
        .. ConfirmVocabulary.Affirm, .. ConfirmVocabulary.Negative, .. ConfirmVocabulary.SayAgain,
        "please", "yes please", "go on then", "love one", "lovely", "no thanks", "not now", "later", "i'm fine", "i'm good",
    ];

    public static CaptainsQuestionPhrases Table { get; } = new(
        Topic: SmallTalkOfferStore.QuestionDefinition.Topic,
        LogName: "Small talk offer",
        MicOwner: "smallTalkOffer",
        EventName: "smalltalk.offerQuestion",
        Grammar: _ => Grammar,
        SpokenPrompt: question => question.Prompt,
        Interpret: (heard, _) => Interpret(heard) switch
        {
            true => QuestionReply.Yes,
            false => QuestionReply.No,
            null => null,
        },
        UnclearPrompt: _ => "Say again, captain — coffee, or you're fine?",
        UnansweredPhrase: (_, _) => "No worries, captain — shout if you change your mind.",
        RaisedPayload: question => new { prompt = question.Prompt, attempt = question.Attempt },
        AnsweredPayload: (_, reply, heard) => new { outcome = reply.Kind == QuestionReplyKind.Yes ? "yes" : "no", heard },
        UnansweredPayload: (_, asking) => new { outcome = "unanswered", open = asking.QuestionStands },
        YesReadback: "I'll get the cabin to send something up.",
        NoReadback: "No problem, captain.");

    /// <summary>true = yes please, false = not now, null = not an answer. Pure, for tests.</summary>
    public static bool? Interpret(string heard)
    {
        ArgumentNullException.ThrowIfNull(heard);
        var text = CommandVocabularyNormalize(heard);
        if (text.Length == 0)
        {
            return null;
        }

        if (text.Contains("no thanks", StringComparison.Ordinal) || text.Contains("not now", StringComparison.Ordinal)
            || text == "later" || text.Contains("i m fine", StringComparison.Ordinal) || text.Contains("i m good", StringComparison.Ordinal))
        {
            return false;
        }

        if (text == "please" || text.Contains("yes please", StringComparison.Ordinal) || text.Contains("go on", StringComparison.Ordinal)
            || text.Contains("love one", StringComparison.Ordinal) || text == "lovely")
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

    private static string CommandVocabularyNormalize(string heard) => CommandMatcher.Normalize(heard);
}
