using ProsimCompanion.Core.State;
using ProsimCompanion.Core.TechLog;

namespace ProsimCompanion.Core.Crew;

/// <summary>The raise question's state: the question plus the finding it is about.</summary>
/// <param name="FindingId">The walkaround finding on the table.</param>
/// <param name="Title">The tech-log title that would be raised.</param>
/// <param name="Category">The MEL category it would be raised under.</param>
public sealed record WalkaroundRaiseSnapshot(CaptainsQuestionState Asking, string? FindingId, string? Title, MelCategory Category)
    : ICaptainsQuestionSnapshot<WalkaroundRaiseSnapshot>
{
    public static WalkaroundRaiseSnapshot Empty { get; } = new(CaptainsQuestionState.Empty, null, null, MelCategory.C);

    WalkaroundRaiseSnapshot ICaptainsQuestionSnapshot<WalkaroundRaiseSnapshot>.WithAsking(CaptainsQuestionState asking)
        => this with { Asking = asking };
}

/// <summary>
/// "I'd like to raise it in the tech log — shall I?" (issue #196): the question the walkaround
/// report ends with on a deferrable finding. Adapter over
/// <see cref="CaptainsQuestionStore{TSnapshot}"/>: the walkaround service raises it with the
/// finding's title and category, the one dialogue voices it, yes hands the pre-filled item to
/// the tech-log raise path (which still confirms), no is "noted". The 5-minute TTL counts as
/// no; one re-ask; not final for the cycle.
/// </summary>
public sealed class WalkaroundRaiseStore : CaptainsQuestionStore<WalkaroundRaiseSnapshot>
{
    public const string Prompt = "I'd like to raise it in the tech log — shall I?";

    public static CaptainsQuestionDefinition QuestionDefinition { get; } = new(
        "walkaroundRaise",
        CaptainsQuestionShape.YesNo,
        CaptainsQuestionDefinition.StandardTtl,
        CaptainsQuestionExpiry.CountsAsNo,
        CaptainsQuestionDefinition.StandardMaxAsks,
        AnswerIsFinalForCycle: false,
        TimeoutReasonSuffix: " — the finding stays noted, not raised");

    public WalkaroundRaiseStore(GroundOpsSignals signals)
        : base(QuestionDefinition, WalkaroundRaiseSnapshot.Empty, signals)
    {
    }

    /// <summary>Raises the question for a finding; false while one already stands.</summary>
    public bool Ask(string findingId, string title, MelCategory category)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(findingId);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        return Commit(s => s with
        {
            FindingId = findingId,
            Title = title,
            Category = category,
            Asking = CaptainsQuestionRules.Ask(s.Asking, Definition, Prompt, [], null, null, DateTimeOffset.UtcNow),
        }).Raised is not null;
    }
}
