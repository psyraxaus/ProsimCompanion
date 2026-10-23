namespace ProsimCompanion.Core.State;

/// <summary>The threats question's state: the open question plus every threat the captain
/// named this cycle, in the order heard (issue #180).</summary>
/// <param name="Asking">The shared question part.</param>
/// <param name="CaptainsThreats">The captain's own words, one entry per item named.</param>
public sealed record ThreatsQuestionSnapshot(CaptainsQuestionState Asking, IReadOnlyList<string> CaptainsThreats)
    : ICaptainsQuestionSnapshot<ThreatsQuestionSnapshot>
{
    public static ThreatsQuestionSnapshot Empty { get; } = new(CaptainsQuestionState.Empty, []);

    ThreatsQuestionSnapshot ICaptainsQuestionSnapshot<ThreatsQuestionSnapshot>.WithAsking(CaptainsQuestionState asking)
        => this with { Asking = asking };
}

/// <summary>
/// The "any other threats, captain?" question the First Officer puts after the emergency half
/// of the takeoff briefing (issue #180). Adapter over <see cref="CaptainsQuestionStore{TSnapshot}"/>:
/// the briefing raises it, the one dialogue voices it, the captain answers in free text — a
/// named threat is noted (<see cref="Note"/>) and the question counts as answered; "no" /
/// "nothing else" declines it. Silence leaves it open under the #166 rules (the 5-minute TTL
/// counts as no, one re-ask). Not final for the cycle: a second briefing after a runway change
/// asks again, and the threats already named are kept.
/// </summary>
public sealed class ThreatsQuestionStore : CaptainsQuestionStore<ThreatsQuestionSnapshot>
{
    /// <summary>The FO's line; one fixed prompt, so the probe can grep it.</summary>
    public const string Prompt = "Any other threats, captain?";

    /// <summary>Yes/no shape (yes = a threat was named), the standard TTL counting as no,
    /// the one re-ask, not final for the cycle.</summary>
    public static CaptainsQuestionDefinition QuestionDefinition { get; } = new(
        "briefingThreats",
        CaptainsQuestionShape.YesNo,
        CaptainsQuestionDefinition.StandardTtl,
        CaptainsQuestionExpiry.CountsAsNo,
        CaptainsQuestionDefinition.StandardMaxAsks,
        AnswerIsFinalForCycle: false,
        TimeoutReasonSuffix: " — no further threats");

    public ThreatsQuestionStore(GroundOpsSignals signals)
        : base(QuestionDefinition, ThreatsQuestionSnapshot.Empty, signals)
    {
    }

    /// <summary>The threats the captain has named this cycle.</summary>
    public IReadOnlyList<string> CaptainsThreats => Snapshot().CaptainsThreats;

    /// <summary>Raises the question after a briefing; false while one already stands.</summary>
    public bool Ask() => Raise(Prompt) is not null;

    /// <summary>The captain named a threat: it is appended to the list and the question is
    /// answered yes. True when the state changed (an empty utterance changes nothing).</summary>
    public bool Note(string threat, string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        var text = threat?.Trim() ?? "";
        if (text.Length == 0)
        {
            return false;
        }

        return Commit(s => s with
        {
            CaptainsThreats = [.. s.CaptainsThreats, text],
            Asking = CaptainsQuestionRules.Accept(s.Asking, source, DateTimeOffset.UtcNow),
        }).Answered is not null;
    }
}
