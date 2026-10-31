using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Persona;

/// <summary>The "swap First Officer?" question's state: the question part alone.</summary>
public sealed record FoRosterChangeSnapshot(CaptainsQuestionState Asking) : ICaptainsQuestionSnapshot<FoRosterChangeSnapshot>
{
    public static FoRosterChangeSnapshot Empty { get; } = new(CaptainsQuestionState.Empty);

    FoRosterChangeSnapshot ICaptainsQuestionSnapshot<FoRosterChangeSnapshot>.WithAsking(CaptainsQuestionState asking)
        => this with { Asking = asking };
}

/// <summary>
/// The captain's question behind "new first officer" (issue #194): a mid-day re-draw swaps the
/// person who has been flying with the captain, so the FO confirms before the roster draws
/// again. Adapter over <see cref="CaptainsQuestionStore{TSnapshot}"/>: the voice feature
/// raises it, the one dialogue voices it, yes draws, no leaves the crew as it is. The 5-minute
/// TTL counts as no, one re-ask, not final for the cycle (the captain may ask again later).
/// </summary>
public sealed class FoRosterChangeStore : CaptainsQuestionStore<FoRosterChangeSnapshot>
{
    /// <summary>The FO's line; fixed so the probe can grep it.</summary>
    public const string Prompt = "Change First Officer now, captain — are you sure?";

    public static CaptainsQuestionDefinition QuestionDefinition { get; } = new(
        "foRosterChange",
        CaptainsQuestionShape.YesNo,
        CaptainsQuestionDefinition.StandardTtl,
        CaptainsQuestionExpiry.CountsAsNo,
        CaptainsQuestionDefinition.StandardMaxAsks,
        AnswerIsFinalForCycle: false,
        TimeoutReasonSuffix: " — the First Officer stays");

    public FoRosterChangeStore(GroundOpsSignals signals)
        : base(QuestionDefinition, FoRosterChangeSnapshot.Empty, signals)
    {
    }

    /// <summary>Raises the question; false while one already stands.</summary>
    public bool Ask() => Raise(Prompt) is not null;
}
