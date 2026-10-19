namespace ProsimCompanion.Core.State;

/// <summary>What the de-icing policy concluded for this departure.</summary>
public enum DeicePolicyVerdict
{
    /// <summary>The policy has not run for this cycle yet (it runs when the DeIce step's turn
    /// comes, or at boarding complete — <c>gsx.deice.askWhen</c>).</summary>
    Pending,

    /// <summary><c>gsx.deice.autoRequest</c> is off — the policy never decides.</summary>
    Off,

    /// <summary>Not enough data: no outside air temperature, or precipitation required and
    /// no METAR for the departure airport.</summary>
    NoData,

    /// <summary>Conditions do not call for de-icing.</summary>
    NotRequired,

    /// <summary>Conditions call for de-icing and the captain is asked (mode <c>ask</c>).</summary>
    Ask,

    /// <summary>Conditions call for de-icing and the app requests it (mode <c>auto</c>).</summary>
    Request,
}

/// <summary>The de-icing decision for the current departure cycle.</summary>
/// <param name="Verdict">The policy's conclusion.</param>
/// <param name="Reason">One sentence saying why, as the decision log records it.</param>
/// <param name="OatC">The outside air temperature the verdict used (ProSim, else METAR).</param>
/// <param name="Precip">The departure METAR's precipitation word ("snow", "none", …).</param>
/// <param name="Icao">The departure airport the weather was read for.</param>
/// <param name="Asking">The captain's question and answer (the shared captain's-question
/// state): the open "request de-icing?" question, the captain's yes (de-icing goes on the
/// sequence) or no (the step is skipped with the reason for the rest of the cycle).</param>
/// <param name="DecidedAtUtc">When the verdict was published.</param>
/// <param name="StepNext">True once the departure sequencer reported the DeIce step's turn
/// has come (issue #166): the policy runs no earlier than this in <c>whenStepIsNext</c>
/// mode, and a question asked earlier (<c>afterBoardingComplete</c>) is re-asked once here.</param>
public sealed record DeiceRequestSnapshot(
    DeicePolicyVerdict Verdict,
    string Reason,
    double? OatC,
    string Precip,
    string? Icao,
    CaptainsQuestionState Asking,
    DateTimeOffset? DecidedAtUtc,
    bool StepNext = false) : ICaptainsQuestionSnapshot<DeiceRequestSnapshot>
{
    public static DeiceRequestSnapshot Empty { get; } =
        new(DeicePolicyVerdict.Pending, "not evaluated yet", null, "unknown", null, CaptainsQuestionState.Empty, null);

    /// <summary>The default question TTL (<c>gsx.deice.questionTimeoutMinutes</c> overrides it
    /// per question): an unanswered question past it counts as declined, so a departure with
    /// nobody at the FO's mic or the web page never waits forever (degrade, not fail). The
    /// captain can still say "request de-icing" afterwards.</summary>
    public static TimeSpan QuestionTtl { get; } = CaptainsQuestionDefinition.StandardTtl;

    /// <summary>True once de-icing is to be placed on the departure sequence: an <c>auto</c>
    /// verdict, or the captain's "yes".</summary>
    public bool RequestThisCycle => Asking.Accepted;

    /// <summary>Set when the captain answered "no" (or nobody answered in time) — the step is
    /// skipped with this reason for the rest of the cycle.</summary>
    public string? Declined => Asking.Declined;

    /// <summary>The open question, null when none stands.</summary>
    public CaptainsQuestion? Question => Asking.Question;

    /// <summary>True while a question stands and its TTL has not passed.</summary>
    public bool QuestionOpen(DateTimeOffset nowUtc) => Asking.QuestionOpen(nowUtc);

    /// <summary>True while the policy has not decided and the sequencer has not yet reported
    /// the step's turn — the DeIce step is kept on the board (last, held) so the departure
    /// cannot complete under a verdict that has not been reached.</summary>
    public bool Undecided => Verdict == DeicePolicyVerdict.Pending && !RequestThisCycle && Declined is null && !StepNext;

    /// <inheritdoc />
    public DeiceRequestSnapshot WithAsking(CaptainsQuestionState asking)
    {
        ArgumentNullException.ThrowIfNull(asking);
        return this with { Asking = asking };
    }
}

/// <summary>
/// Per-departure de-icing decision (2026-10-09). Written by the GSX pillar's policy service
/// (the verdict, the question) and by whoever answers the captain's question — the FO voice
/// dialogue, the Status board buttons, a voice command, or a direct de-ice call — and read
/// by the departure sequencer. Resets with the flight cycle. Kept in Core so Web and Speech
/// can read it without a pillar reference (feature projects never reference each other).
/// <para>Adapter over <see cref="CaptainsQuestionStore{TSnapshot}"/> (architecture review C2):
/// the verdict facts live here, the question rules in the base. This is the question that
/// taught the base its rules — issue #166 (EFHK 2026-10-10): the TTL as the only clock that
/// declines, the one re-ask when the step's turn comes, and "silence is not an answer".</para>
/// </summary>
public sealed class DeiceRequestStore : CaptainsQuestionStore<DeiceRequestSnapshot>
{
    /// <summary>Yes/no, final for the cycle, the 5-minute TTL (<c>gsx.deice.questionTimeoutMinutes</c>
    /// overrides it per question) and the one re-ask; the timeout reason names the setting
    /// (probe <c>gsx-deice-policy</c>: "no answer within N minutes").</summary>
    public static CaptainsQuestionDefinition QuestionDefinition { get; } = new(
        "deice",
        CaptainsQuestionShape.YesNo,
        CaptainsQuestionDefinition.StandardTtl,
        CaptainsQuestionExpiry.CountsAsNo,
        CaptainsQuestionDefinition.StandardMaxAsks,
        AnswerIsFinalForCycle: true,
        TimeoutReasonSuffix: " (gsx.deice.questionTimeoutMinutes)");

    public DeiceRequestStore(GroundOpsSignals signals)
        : base(QuestionDefinition, DeiceRequestSnapshot.Empty, signals)
    {
    }

    /// <summary>Raised once per cycle when the sequencer reports the DeIce step's turn has
    /// come (<see cref="MarkStepNext"/>) — the policy service evaluates on it.</summary>
    public event EventHandler? StepBecameNext;

    /// <summary>Publishes the policy's verdict. An <c>Ask</c> verdict raises the question
    /// (once — a re-evaluation with the same verdict keeps the open question); a
    /// <c>Request</c> verdict marks the cycle; anything else only records itself. A verdict
    /// never un-does an answer already given this cycle.</summary>
    /// <param name="questionTtl">How long the question stands (null = <see cref="DeiceRequestSnapshot.QuestionTtl"/>).</param>
    public void Publish(DeicePolicyVerdict verdict, string reason, double? oatC, string precip, string? icao, string prompt, TimeSpan? questionTtl = null)
    {
        ArgumentNullException.ThrowIfNull(reason);
        ArgumentNullException.ThrowIfNull(precip);
        ArgumentNullException.ThrowIfNull(prompt);

        // The auto verdict is the policy's decision, not the captain's answer: recorded with
        // source "policy" and no Answered edge (the live event stream has no gsx-deice-policy
        // line for it, 2026-10-09).
        Commit(
            s =>
            {
                var now = DateTimeOffset.UtcNow;
                var next = s with
                {
                    Verdict = verdict,
                    Reason = reason,
                    OatC = oatC,
                    Precip = precip,
                    Icao = icao,
                    DecidedAtUtc = now,
                };

                if (s.Asking.Answer is not null)
                {
                    return next;
                }

                return verdict switch
                {
                    DeicePolicyVerdict.Request => next.WithAsking(CaptainsQuestionRules.Accept(next.Asking, "policy", now)),
                    DeicePolicyVerdict.Ask => next.WithAsking(CaptainsQuestionRules.Ask(next.Asking, Definition, prompt, [], null, questionTtl, now)),
                    // Conditions no longer call for it (or no data): withdraw an unanswered
                    // question rather than leave the departure holding on a stale one.
                    _ => next.WithAsking(CaptainsQuestionRules.Withdraw(next.Asking)),
                };
            },
            announceAnswer: false);
    }

    /// <summary>The departure sequencer reports the DeIce step's turn has come (every earlier
    /// service done or skipped, the plan gate passed). Idempotent per cycle. A question that
    /// was asked earlier and still stands is re-asked once, with a fresh TTL — the captain
    /// may have been busy at boarding complete; now it is the only thing left.</summary>
    public void MarkStepNext()
    {
        var first = false;
        Commit(s =>
        {
            if (s.StepNext)
            {
                return s;
            }

            first = true;
            return (s with { StepNext = true }).WithAsking(CaptainsQuestionRules.ReAsk(s.Asking, Definition, DateTimeOffset.UtcNow));
        });

        if (first)
        {
            StepBecameNext?.Invoke(this, EventArgs.Empty);
        }
    }
}
