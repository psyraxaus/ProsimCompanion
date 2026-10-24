namespace ProsimCompanion.Core.State;

/// <summary>
/// The purser's "it's getting bumpy back here — should we secure the cabin?" (issue #186): a
/// yes/no captain's question raised once per turbulence episode at Moderate when
/// <c>cabin.turbulencePurserCall</c> is on. Yes → the purser seats the crew and reports the
/// cabin secured 30–90 s later; no → the service continues; silence → the TTL counts as no
/// (#166 rules, shared since C2). Not final for the cycle: the next episode may ask again.
/// Adapter over <see cref="CaptainsQuestionStore{TSnapshot}"/>.
/// </summary>
public sealed class CabinSecureQuestionStore : CaptainsQuestionStore<CaptainsQuestionState>
{
    public static CaptainsQuestionDefinition QuestionDefinition { get; } = new(
        "cabinSecure",
        CaptainsQuestionShape.YesNo,
        CaptainsQuestionDefinition.StandardTtl,
        CaptainsQuestionExpiry.CountsAsNo,
        CaptainsQuestionDefinition.StandardMaxAsks,
        AnswerIsFinalForCycle: false);

    public CabinSecureQuestionStore(GroundOpsSignals signals)
        : base(QuestionDefinition, CaptainsQuestionState.Empty, signals)
    {
    }

    /// <summary>Raises the question; false while one stands.</summary>
    public bool Ask(string prompt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        return Raise(prompt) is not null;
    }

    /// <summary>Clears the last episode's answer so the next episode can ask afresh.</summary>
    public void NewEpisode() => Reset();
}
