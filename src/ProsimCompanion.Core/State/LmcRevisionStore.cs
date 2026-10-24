namespace ProsimCompanion.Core.State;

/// <summary>
/// The "boarding differs from the final loadsheet — issue a revised final?" question (issue
/// #181). Raised by the loadsheet pipeline when a last-minute change is beyond tolerance and
/// <c>flightData.lmcAutoRevise</c> is off; yes re-runs the final path with EDNO +1, no (or
/// the 5-minute TTL, #166) leaves the LMC block under the final with the "flown on captain's
/// authority" caption. Adapter over <see cref="CaptainsQuestionStore{TSnapshot}"/>; not final
/// for the cycle — a second LMC (an on-demand re-check after more boarding) may ask again.
/// </summary>
public sealed class LmcRevisionStore : CaptainsQuestionStore<CaptainsQuestionState>
{
    public static CaptainsQuestionDefinition QuestionDefinition { get; } = new(
        "loadsheetRevision",
        CaptainsQuestionShape.YesNo,
        CaptainsQuestionDefinition.StandardTtl,
        CaptainsQuestionExpiry.CountsAsNo,
        CaptainsQuestionDefinition.StandardMaxAsks,
        AnswerIsFinalForCycle: false,
        TimeoutReasonSuffix: " — flown on captain's authority");

    public LmcRevisionStore(GroundOpsSignals signals)
        : base(QuestionDefinition, CaptainsQuestionState.Empty, signals)
    {
    }

    /// <summary>Raises the question with the LMC figures in the prompt; false while one stands.</summary>
    public bool Ask(string prompt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        return Raise(prompt) is not null;
    }
}
