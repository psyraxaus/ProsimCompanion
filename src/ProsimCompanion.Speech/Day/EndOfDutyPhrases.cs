using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Gsx;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Day;

/// <summary>
/// The FO's "Captain, is that the end of our duty day?" (ADR-0019 §8, issue #217): the duty-day
/// service raises the <see cref="EndOfDutyOfferStore"/> question once the aircraft has been
/// cold and dark, parked, with no plan for the configured minutes; the shared
/// <see cref="CaptainsQuestionDialogue"/> voices it from this table. Yes ends the day through
/// the store's answer; no (or nobody answering, or the TTL) leaves the day open and the offer
/// is not made again that day. "End duty day" and the web button still work at any time.
/// </summary>
public static class EndOfDutyPhrases
{
    /// <summary>The session event the dialogue records for the offer and its answer; the
    /// probe <c>day-end-offer</c> reads it.</summary>
    public const string EventName = "day.end-offer";

    private static readonly IReadOnlyList<string> Grammar =
    [
        .. ConfirmVocabulary.Affirm, .. ConfirmVocabulary.Negative, .. ConfirmVocabulary.SayAgain,
        "that's us", "that is us", "we're done", "we are done", "end duty day", "end the duty day",
        "not yet", "one more", "another sector", "keep it open", "leave it open",
    ];

    /// <summary>The table the shared dialogue voices the offer from.</summary>
    public static CaptainsQuestionPhrases Table { get; } = new(
        Topic: EndOfDutyOfferStore.QuestionDefinition.Topic,
        LogName: "End-of-duty offer",
        MicOwner: "endOfDuty",
        EventName: EventName,
        Grammar: _ => Grammar,
        SpokenPrompt: question => question.Prompt,
        Interpret: (heard, _) => Interpret(heard) switch
        {
            true => QuestionReply.Yes,
            false => QuestionReply.No,
            null => null,
        },
        UnclearPrompt: _ => "Say again — is that the end of our duty day, or do we keep it open?",
        UnansweredPhrase: (_, _) => "Your call — say end duty day when you are done.",
        RaisedPayload: question => new { prompt = question.Prompt },
        AnsweredPayload: (_, reply, heard) => new { outcome = reply.Kind == QuestionReplyKind.Yes ? "accepted" : "declined", heard },
        UnansweredPayload: (_, _) => new { outcome = "unanswered" },
        YesReadback: "Copied — ending the duty day.",
        NoReadback: "Copied — the day stays open.");

    /// <summary>Classifies one heard utterance: true = end the day, false = keep it open, null =
    /// not an answer. Pure, for tests. <see cref="CommandMatcher.Normalize"/> turns every
    /// apostrophe into a space ("that's us" → "that s us"), so the phrases are matched in that form.</summary>
    public static bool? Interpret(string heard)
    {
        ArgumentNullException.ThrowIfNull(heard);
        var text = CommandMatcher.Normalize(heard);
        if (text.Contains("not yet", StringComparison.Ordinal) || text.Contains("one more", StringComparison.Ordinal)
            || text.Contains("another sector", StringComparison.Ordinal) || text.Contains("keep it open", StringComparison.Ordinal)
            || text.Contains("leave it open", StringComparison.Ordinal))
        {
            return false;
        }

        if (text.Contains("end duty day", StringComparison.Ordinal) || text.Contains("end the duty day", StringComparison.Ordinal)
            || text.Contains("that s us", StringComparison.Ordinal) || text.Contains("thats us", StringComparison.Ordinal)
            || text.Contains("that is us", StringComparison.Ordinal)
            || text.Contains("we re done", StringComparison.Ordinal) || text.Contains("were done", StringComparison.Ordinal)
            || text.Contains("we are done", StringComparison.Ordinal))
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
