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

/// <summary>An open "request de-icing?" question for the captain. The FO voices it; the
/// Status board shows two buttons; the departure sequence holds the DeIce step until it is
/// answered or <see cref="Ttl"/> passes.</summary>
/// <param name="AskedAtUtc">When this attempt was asked (the TTL counts from here — a
/// re-ask restarts it).</param>
/// <param name="Prompt">The FO's words.</param>
/// <param name="Ttl">How long the question stands before it counts as declined.</param>
/// <param name="Attempt">1 for the first ask, 2 for the one re-ask when the step's turn comes.</param>
public sealed record DeiceQuestion(DateTimeOffset AskedAtUtc, string Prompt, TimeSpan Ttl, int Attempt = 1)
{
    /// <summary>Test/legacy convenience: a question with the default TTL.</summary>
    public DeiceQuestion(DateTimeOffset askedAtUtc, string prompt)
        : this(askedAtUtc, prompt, DeiceRequestSnapshot.QuestionTtl)
    {
    }

    /// <summary>When this attempt expires.</summary>
    public DateTimeOffset ExpiresAtUtc => AskedAtUtc + Ttl;
}

/// <summary>The de-icing decision for the current departure cycle.</summary>
/// <param name="Verdict">The policy's conclusion.</param>
/// <param name="Reason">One sentence saying why, as the decision log records it.</param>
/// <param name="OatC">The outside air temperature the verdict used (ProSim, else METAR).</param>
/// <param name="Precip">The departure METAR's precipitation word ("snow", "none", …).</param>
/// <param name="Icao">The departure airport the weather was read for.</param>
/// <param name="RequestThisCycle">True once de-icing is to be placed on the departure
/// sequence: an <c>auto</c> verdict, or the captain's "yes".</param>
/// <param name="Declined">Set when the captain answered "no" (or nobody answered in time)
/// — the step is skipped with this reason for the rest of the cycle.</param>
/// <param name="Question">The open question, null when none stands.</param>
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
    bool RequestThisCycle,
    string? Declined,
    DeiceQuestion? Question,
    DateTimeOffset? DecidedAtUtc,
    bool StepNext = false)
{
    public static DeiceRequestSnapshot Empty { get; } =
        new(DeicePolicyVerdict.Pending, "not evaluated yet", null, "unknown", null, false, null, null, null);

    /// <summary>The default question TTL (<c>gsx.deice.questionTimeoutMinutes</c> overrides it
    /// per question): an unanswered question past it counts as declined, so a departure with
    /// nobody at the FO's mic or the web page never waits forever (degrade, not fail). The
    /// captain can still say "request de-icing" afterwards.</summary>
    public static TimeSpan QuestionTtl { get; } = TimeSpan.FromMinutes(5);

    /// <summary>True while a question stands and its TTL has not passed.</summary>
    public bool QuestionOpen(DateTimeOffset nowUtc)
        => Question is { } q && nowUtc - q.AskedAtUtc < q.Ttl;

    /// <summary>True while the policy has not decided and the sequencer has not yet reported
    /// the step's turn — the DeIce step is kept on the board (last, held) so the departure
    /// cannot complete under a verdict that has not been reached.</summary>
    public bool Undecided => Verdict == DeicePolicyVerdict.Pending && !RequestThisCycle && Declined is null && !StepNext;
}

/// <summary>
/// Per-departure de-icing decision (2026-10-09). Written by the GSX pillar's policy service
/// (the verdict, the question) and by whoever answers the captain's question — the FO voice
/// dialogue, the Status board buttons, a voice command, or a direct de-ice call — and read
/// by the departure sequencer. Resets with the flight cycle. Kept in Core so Web and Speech
/// can read it without a pillar reference (feature projects never reference each other).
/// </summary>
public sealed class DeiceRequestStore : SnapshotStore<DeiceRequestSnapshot>, IDisposable
{
    private readonly GroundOpsSignals _signals;

    public DeiceRequestStore(GroundOpsSignals signals)
        : base(DeiceRequestSnapshot.Empty)
    {
        ArgumentNullException.ThrowIfNull(signals);
        _signals = signals;
        _signals.FlightCycleReset += Reset;
    }

    /// <summary>Raised once per ask — the Speech pillar voices it. A question asked before the
    /// step's turn is raised a second time (Attempt 2) when the turn comes.</summary>
    public event EventHandler<DeiceQuestion>? QuestionRaised;

    /// <summary>Raised when the captain's answer (or its absence) settles the question:
    /// true = request de-icing. The argument names who answered ("voice", "web", "called",
    /// "timeout").</summary>
    public event EventHandler<(bool Accepted, string Source)>? Answered;

    /// <summary>Raised once per cycle when the sequencer reports the DeIce step's turn has
    /// come (<see cref="MarkStepNext"/>) — the policy service evaluates on it.</summary>
    public event EventHandler? StepBecameNext;

    public void Dispose() => _signals.FlightCycleReset -= Reset;

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

        DeiceQuestion? raised = null;
        Update(s =>
        {
            var answered = s.RequestThisCycle || s.Declined is not null;
            var next = s with
            {
                Verdict = verdict,
                Reason = reason,
                OatC = oatC,
                Precip = precip,
                Icao = icao,
                DecidedAtUtc = DateTimeOffset.UtcNow,
            };

            if (answered)
            {
                return next;
            }

            switch (verdict)
            {
                case DeicePolicyVerdict.Request:
                    return next with { RequestThisCycle = true, Question = null };
                case DeicePolicyVerdict.Ask when s.Question is null:
                    raised = new DeiceQuestion(DateTimeOffset.UtcNow, prompt, questionTtl ?? DeiceRequestSnapshot.QuestionTtl);
                    return next with { Question = raised };
                case DeicePolicyVerdict.Ask:
                    return next;
                default:
                    // Conditions no longer call for it (or no data): withdraw an unanswered
                    // question rather than leave the departure holding on a stale one.
                    return next with { Question = null };
            }
        });

        if (raised is not null)
        {
            QuestionRaised?.Invoke(this, raised);
        }
    }

    /// <summary>The departure sequencer reports the DeIce step's turn has come (every earlier
    /// service done or skipped, the plan gate passed). Idempotent per cycle. A question that
    /// was asked earlier and still stands is re-asked once, with a fresh TTL — the captain
    /// may have been busy at boarding complete; now it is the only thing left.</summary>
    public void MarkStepNext()
    {
        var first = false;
        DeiceQuestion? reask = null;
        Update(s =>
        {
            if (s.StepNext)
            {
                return s;
            }

            first = true;
            if (s.Question is { Attempt: 1 } q && QuestionOpenAt(q, DateTimeOffset.UtcNow))
            {
                reask = q with { AskedAtUtc = DateTimeOffset.UtcNow, Attempt = 2 };
                return s with { StepNext = true, Question = reask };
            }

            return s with { StepNext = true };
        });

        if (!first)
        {
            return;
        }

        if (reask is not null)
        {
            QuestionRaised?.Invoke(this, reask);
        }

        StepBecameNext?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The captain wants de-icing (voice "yes"/"request de-icing", the web button,
    /// or a direct call observed on the mirror). Idempotent.</summary>
    public void Accept(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        var changed = false;
        Update(s =>
        {
            if (s.RequestThisCycle)
            {
                return s;
            }

            changed = true;
            return s with { RequestThisCycle = true, Declined = null, Question = null };
        });

        if (changed)
        {
            Answered?.Invoke(this, (true, source));
        }
    }

    /// <summary>The captain declined (or the question timed out). Only meaningful while a
    /// question stands or a request has not been placed yet; idempotent.</summary>
    public void Decline(string source, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var changed = false;
        Update(s =>
        {
            if (s.RequestThisCycle || s.Declined is not null)
            {
                return s;
            }

            changed = true;
            return s with { Declined = reason, Question = null };
        });

        if (changed)
        {
            Answered?.Invoke(this, (false, source));
        }
    }

    public void Reset() => Update(_ => DeiceRequestSnapshot.Empty);

    private static bool QuestionOpenAt(DeiceQuestion question, DateTimeOffset nowUtc)
        => nowUtc - question.AskedAtUtc < question.Ttl;
}
