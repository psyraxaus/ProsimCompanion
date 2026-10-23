using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Gsx;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Cabin;

/// <summary>
/// The "secure the cabin?" question (issue #186), voiced by the shared
/// <see cref="CaptainsQuestionDialogue"/>. The purser has already asked over the interphone
/// (the cabin call the reaction rings first); the FO's spoken prompt is the relay — short, so the
/// two do not say the same sentence twice. Yes = seat the crew (the purser acknowledges and the
/// cabin reports secured 30–90 s later); no = carry on with the service; silence = the TTL
/// counts as no.
/// </summary>
public static class CabinSecureQuestionPhrases
{
    private static readonly IReadOnlyList<string> Grammar =
    [
        .. ConfirmVocabulary.Affirm, .. ConfirmVocabulary.Negative, .. ConfirmVocabulary.SayAgain,
        "secure the cabin", "secure cabin", "seat the crew", "crew be seated", "be seated", "sit down",
        "carry on", "continue the service", "continue service", "keep going", "not yet", "stand by", "standby", "cancel",
    ];

    public static CaptainsQuestionPhrases Table { get; } = new(
        Topic: CabinSecureQuestionStore.QuestionDefinition.Topic,
        LogName: "Cabin secure question",
        MicOwner: "cabinSecure",
        EventName: "cabin-secure-question",
        Grammar: _ => Grammar,
        SpokenPrompt: _ => "Cabin asks — secure the cabin, captain?",
        Interpret: (heard, _) => Interpret(heard) switch
        {
            true => QuestionReply.Yes,
            false => QuestionReply.No,
            null => null,
        },
        UnclearPrompt: _ => "Say again — secure the cabin, or carry on with the service?",
        UnansweredPhrase: (_, _) => "Your call — say secure the cabin when you want the crew seated.",
        RaisedPayload: question => new { prompt = question.Prompt, attempt = question.Attempt },
        AnsweredPayload: (_, reply, heard) => new { outcome = reply.Kind == QuestionReplyKind.Yes ? "accepted" : "declined", heard },
        UnansweredPayload: (_, _) => new { outcome = "unanswered" },
        YesReadback: "Copied — cabin, secure the cabin, crew be seated.",
        NoReadback: "Copied — cabin, carry on with the service.");

    /// <summary>true = secure, false = carry on, null = not an answer. Pure, for tests.</summary>
    public static bool? Interpret(string heard)
    {
        ArgumentNullException.ThrowIfNull(heard);
        var text = CommandMatcher.Normalize(heard);
        if (text.Contains("carry on", StringComparison.Ordinal) || text.Contains("continue", StringComparison.Ordinal)
            || text.Contains("keep going", StringComparison.Ordinal) || text.Contains("not yet", StringComparison.Ordinal)
            || text.Contains("stand by", StringComparison.Ordinal) || text.Contains("standby", StringComparison.Ordinal)
            || text == "cancel")
        {
            return false;
        }

        if (text.Contains("secure", StringComparison.Ordinal) || text.Contains("seated", StringComparison.Ordinal)
            || text.Contains("seat the crew", StringComparison.Ordinal) || text.Contains("sit down", StringComparison.Ordinal))
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
