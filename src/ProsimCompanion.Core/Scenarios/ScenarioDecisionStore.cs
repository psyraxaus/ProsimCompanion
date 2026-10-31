using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Scenarios;

/// <summary>The decision question's state: the question plus the scenario it is about.</summary>
public sealed record ScenarioDecisionSnapshot(CaptainsQuestionState Asking, string? ScenarioId, string? Title)
    : ICaptainsQuestionSnapshot<ScenarioDecisionSnapshot>
{
    public static ScenarioDecisionSnapshot Empty { get; } = new(CaptainsQuestionState.Empty, null, null);

    ScenarioDecisionSnapshot ICaptainsQuestionSnapshot<ScenarioDecisionSnapshot>.WithAsking(CaptainsQuestionState asking)
        => this with { Asking = asking };
}

/// <summary>
/// The FO's decision turn after a scenario's ECAM actions (issue #197) — "we've lost GEN 1,
/// I'd suggest we continue; your call, captain" — as a pick-one captain's question: continue,
/// return, divert. Adapter over <see cref="CaptainsQuestionStore{TSnapshot}"/>: the scenario
/// service raises it with the composed line, the one dialogue voices it, the web card shows
/// the three buttons; the 5-minute TTL settles it as "continue" (nothing changes), one re-ask.
/// </summary>
public sealed class ScenarioDecisionStore : CaptainsQuestionStore<ScenarioDecisionSnapshot>
{
    public const string Continue = "continue";
    public const string Return = "return";
    public const string Divert = "divert";

    public static IReadOnlyList<QuestionChoice> Choices { get; } =
    [
        new(Continue, "continue", "carry on to the destination"),
        new(Return, "return", "back to the departure airport"),
        new(Divert, "divert", "to the alternate"),
    ];

    public static CaptainsQuestionDefinition QuestionDefinition { get; } = new(
        "scenarioDecision",
        CaptainsQuestionShape.PickOne,
        CaptainsQuestionDefinition.StandardTtl,
        CaptainsQuestionExpiry.CountsAsNo,
        CaptainsQuestionDefinition.StandardMaxAsks,
        AnswerIsFinalForCycle: false,
        TimeoutReasonSuffix: " — we continue");

    public ScenarioDecisionStore(GroundOpsSignals signals)
        : base(QuestionDefinition, ScenarioDecisionSnapshot.Empty, signals)
    {
    }

    /// <summary>Raises the decision for a scenario with the FO's composed line as the prompt;
    /// false while one already stands.</summary>
    public bool Ask(string scenarioId, string title, string prompt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scenarioId);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        return Commit(s => s with
        {
            ScenarioId = scenarioId,
            Title = title,
            Asking = CaptainsQuestionRules.Ask(s.Asking, Definition, prompt, Choices, scenarioId, null, DateTimeOffset.UtcNow),
        }).Raised is not null;
    }

    /// <summary>The captain's pick as one of the three words; "continue" when nothing was picked.</summary>
    public string Outcome => Snapshot().Asking.Answer?.Chosen?.Label ?? Continue;
}
