namespace ProsimCompanion.Core.State;

/// <summary>
/// Per-turnaround "commence ground services?" question (issue #169). Raised by the GSX
/// automation when <c>gsx.groundPrepActivation = voiceThenAuto</c> with
/// <c>gsx.turnaroundAskFirst</c> on and the next leg's plan has settled at the stand; answered
/// by the FO voice dialogue (yes/no) or implicitly by any other start of the sequence. Resets
/// with the flight cycle. Kept in Core so Speech and Gsx share it without referencing each
/// other (feature projects never reference each other).
/// <para>Adapter over <see cref="CaptainsQuestionStore{TSnapshot}"/> (architecture review C2):
/// the shared question state is the whole snapshot. The #166 rules now hold here too — the
/// FO's unanswered listens leave the question open (before C2 they declined it for the
/// cycle), the 5-minute TTL declines it (source <c>timeout</c>), and "commence ground
/// services" / the Start button still answer it implicitly at any time.</para>
/// </summary>
public sealed class TurnaroundStartStore : CaptainsQuestionStore<CaptainsQuestionState>
{
    /// <summary>Yes/no, final for the cycle, the standard TTL and the one re-ask.</summary>
    public static CaptainsQuestionDefinition QuestionDefinition { get; } = new(
        "turnaroundStart",
        CaptainsQuestionShape.YesNo,
        CaptainsQuestionDefinition.StandardTtl,
        CaptainsQuestionExpiry.CountsAsNo,
        CaptainsQuestionDefinition.StandardMaxAsks,
        AnswerIsFinalForCycle: true);

    public TurnaroundStartStore(GroundOpsSignals signals)
        : base(QuestionDefinition, CaptainsQuestionState.Empty, signals)
    {
    }

    /// <summary>Raises the question once per cycle; false while one stands or after an answer
    /// (the sequence started another way).</summary>
    public bool Ask(string prompt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        return Raise(prompt) is not null;
    }
}
