namespace ProsimCompanion.Core.State;

/// <summary>The "any questions, captain?" question's state: the asking skeleton plus how many
/// questions the captain asked this round (for the event and the tests).</summary>
public sealed record BriefingQuestionsSnapshot(CaptainsQuestionState Asking, int QuestionsAsked, bool Departure)
    : ICaptainsQuestionSnapshot<BriefingQuestionsSnapshot>
{
    public static BriefingQuestionsSnapshot Empty { get; } = new(CaptainsQuestionState.Empty, 0, false);

    BriefingQuestionsSnapshot ICaptainsQuestionSnapshot<BriefingQuestionsSnapshot>.WithAsking(CaptainsQuestionState asking)
        => this with { Asking = asking };
}

/// <summary>
/// "Any questions, captain?" after every briefing (owner request 2026-11-01, issue #201). The
/// briefing raises it once the narration — and, on a departure, the threats question — is
/// done; the FO voice dialogue takes the answer: "no questions" closes it with "Briefing
/// complete.", a question is answered by the FO's question service and the FO asks again
/// ("Anything else, captain?"), silence closes it the same way. One minute of silence is the
/// TTL — a briefing never leaves a question standing behind it.
/// </summary>
public sealed class BriefingQuestionsStore : CaptainsQuestionStore<BriefingQuestionsSnapshot>
{
    public const string FirstPrompt = "Any questions, captain?";
    public const string AgainPrompt = "Anything else, captain?";
    public const string CompleteLine = "Briefing complete.";

    /// <summary>At most this many captain's questions per briefing before the FO closes.</summary>
    public const int MaxQuestions = 3;

    public static CaptainsQuestionDefinition QuestionDefinition { get; } = new(
        "briefingQuestions",
        CaptainsQuestionShape.YesNo,
        TimeSpan.FromMinutes(1),
        CaptainsQuestionExpiry.CountsAsNo,
        CaptainsQuestionDefinition.StandardMaxAsks,
        AnswerIsFinalForCycle: false,
        TimeoutReasonSuffix: " — briefing complete");

    public BriefingQuestionsStore(GroundOpsSignals signals)
        : base(QuestionDefinition, BriefingQuestionsSnapshot.Empty, signals)
    {
    }

    /// <summary>Questions the captain asked in the current round.</summary>
    public int QuestionsAsked => Snapshot().QuestionsAsked;

    /// <summary>Whether the round follows the departure briefing (else the arrival).</summary>
    public bool Departure => Snapshot().Departure;

    /// <summary>Opens a round after a briefing: "Any questions, captain?". False when one
    /// stands already.</summary>
    public bool Ask(bool departure)
    {
        if (Asking.QuestionStands)
        {
            return false;
        }

        Update(s => s with { QuestionsAsked = 0, Departure = departure });
        return Raise(FirstPrompt) is not null;
    }

    /// <summary>The captain asked something: counts it, settles the standing question as
    /// answered, and — below <see cref="MaxQuestions"/> — raises "Anything else, captain?".
    /// Returns true when a follow-up was raised.</summary>
    public bool Asked(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        var count = 0;
        Commit(s =>
        {
            count = s.QuestionsAsked + 1;
            return s with
            {
                QuestionsAsked = count,
                Asking = CaptainsQuestionRules.Accept(s.Asking, source, DateTimeOffset.UtcNow),
            };
        });

        return count < MaxQuestions && Raise(AgainPrompt) is not null;
    }
}
