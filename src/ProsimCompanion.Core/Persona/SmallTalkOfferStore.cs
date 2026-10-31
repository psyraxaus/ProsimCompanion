using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Persona;

/// <summary>The offer question's state: the question part alone.</summary>
public sealed record SmallTalkOfferSnapshot(CaptainsQuestionState Asking) : ICaptainsQuestionSnapshot<SmallTalkOfferSnapshot>
{
    public static SmallTalkOfferSnapshot Empty { get; } = new(CaptainsQuestionState.Empty);

    SmallTalkOfferSnapshot ICaptainsQuestionSnapshot<SmallTalkOfferSnapshot>.WithAsking(CaptainsQuestionState asking)
        => this with { Asking = asking };
}

/// <summary>
/// The FO's cruise offer — "Coffee, captain?" (issue #195) — as a captain's question: the
/// small-talk service raises it with the composed line as the prompt, the one dialogue voices
/// it and listens, the web card shows it, and a yes has the purser send something up through
/// the cabin's own call flow. Adapter over <see cref="CaptainsQuestionStore{TSnapshot}"/>:
/// yes/no, the 5-minute TTL counts as no (an unanswered offer is simply dropped), one re-ask,
/// not final for the cycle — a later flight's offer asks again.
/// </summary>
public sealed class SmallTalkOfferStore : CaptainsQuestionStore<SmallTalkOfferSnapshot>
{
    public static CaptainsQuestionDefinition QuestionDefinition { get; } = new(
        "smallTalkOffer",
        CaptainsQuestionShape.YesNo,
        CaptainsQuestionDefinition.StandardTtl,
        CaptainsQuestionExpiry.CountsAsNo,
        CaptainsQuestionDefinition.StandardMaxAsks,
        AnswerIsFinalForCycle: false,
        TimeoutReasonSuffix: " — the offer lapses");

    public SmallTalkOfferStore(GroundOpsSignals signals)
        : base(QuestionDefinition, SmallTalkOfferSnapshot.Empty, signals)
    {
    }

    /// <summary>Raises the offer with the FO's composed line; false while one already stands.</summary>
    public bool Ask(string prompt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        return Raise(prompt) is not null;
    }
}
