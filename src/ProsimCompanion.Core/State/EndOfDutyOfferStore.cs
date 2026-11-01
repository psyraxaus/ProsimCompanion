namespace ProsimCompanion.Core.State;

/// <summary>
/// The FO's one-time "Captain, is that the end of our duty day?" (ADR-0019 §8, issue #217).
/// Raised by the duty-day service once the aircraft has been cold and dark, parked, with no
/// new plan for <c>day.endOfDutyOfferMinutes</c>; answered by the FO voice dialogue (yes/no),
/// the web card or implicitly by "end duty day" / the button. Yes ends the day; no — or the
/// five-minute TTL — leaves it open and the question is never asked again that day (the day
/// state remembers the offer). Kept in Core so Speech asks and the shared dialogue answers
/// without a reference between them.
/// <para>Adapter over <see cref="CaptainsQuestionStore{TSnapshot}"/>: one ask, no re-ask, the
/// standard TTL counts as no (the day stays open; the idle backstop still closes it later).</para>
/// </summary>
public sealed class EndOfDutyOfferStore : CaptainsQuestionStore<CaptainsQuestionState>
{
    /// <summary>Yes/no, one ask, the standard TTL counting as no, final for the cycle.</summary>
    public static CaptainsQuestionDefinition QuestionDefinition { get; } = new(
        "endOfDuty",
        CaptainsQuestionShape.YesNo,
        CaptainsQuestionDefinition.StandardTtl,
        CaptainsQuestionExpiry.CountsAsNo,
        MaxAsks: 1,
        AnswerIsFinalForCycle: true);

    public EndOfDutyOfferStore(GroundOpsSignals signals)
        : base(QuestionDefinition, CaptainsQuestionState.Empty, signals)
    {
    }

    /// <summary>Raises the offer; false while one stands or after an answer this cycle.</summary>
    public bool Ask(string prompt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        return Raise(prompt) is not null;
    }
}
