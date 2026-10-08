namespace ProsimCompanion.Core.State;

/// <summary>What the de-icing policy concluded for this departure.</summary>
public enum DeicePolicyVerdict
{
    /// <summary>The policy has not run for this cycle yet.</summary>
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
/// answered or <see cref="DeiceRequestSnapshot.QuestionTtl"/> passes.</summary>
public sealed record DeiceQuestion(DateTimeOffset AskedAtUtc, string Prompt);

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
public sealed record DeiceRequestSnapshot(
    DeicePolicyVerdict Verdict,
    string Reason,
    double? OatC,
    string Precip,
    string? Icao,
    bool RequestThisCycle,
    string? Declined,
    DeiceQuestion? Question,
    DateTimeOffset? DecidedAtUtc)
{
    public static DeiceRequestSnapshot Empty { get; } =
        new(DeicePolicyVerdict.Pending, "not evaluated yet", null, "unknown", null, false, null, null, null);

    /// <summary>How long an unanswered question stands before it counts as declined, so a
    /// departure with nobody at the FO's mic or the web page never waits forever (degrade,
    /// not fail). The captain can still say "request de-icing" afterwards.</summary>
    public static TimeSpan QuestionTtl { get; } = TimeSpan.FromMinutes(5);

    /// <summary>True while a question stands and its TTL has not passed.</summary>
    public bool QuestionOpen(DateTimeOffset nowUtc)
        => Question is { } q && nowUtc - q.AskedAtUtc < QuestionTtl;
}

/// <summary>
/// Per-departure de-icing decision (2026-10-09). Written by the GSX pillar's policy service
/// (the verdict, the question) and by whoever answers the captain's question — the FO voice
/// dialogue, the Status board buttons, or a direct de-ice call — and read by the departure
/// sequencer. Resets with the flight cycle. Kept in Core so Web and Speech can read it
/// without a pillar reference (feature projects never reference each other).
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

    /// <summary>Raised once per question — the Speech pillar voices it.</summary>
    public event EventHandler<DeiceQuestion>? QuestionRaised;

    /// <summary>Raised when the captain's answer (or its absence) settles the question:
    /// true = request de-icing. The argument names who answered ("voice", "web", "called",
    /// "timeout").</summary>
    public event EventHandler<(bool Accepted, string Source)>? Answered;

    public void Dispose() => _signals.FlightCycleReset -= Reset;

    /// <summary>Publishes the policy's verdict. An <c>Ask</c> verdict raises the question
    /// (once — a re-evaluation with the same verdict keeps the open question); a
    /// <c>Request</c> verdict marks the cycle; anything else only records itself. A verdict
    /// never un-does an answer already given this cycle.</summary>
    public void Publish(DeicePolicyVerdict verdict, string reason, double? oatC, string precip, string? icao, string prompt)
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
                    raised = new DeiceQuestion(DateTimeOffset.UtcNow, prompt);
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

    /// <summary>The captain declined (or nobody answered). Only meaningful while a question
    /// stands or a request has not been placed yet; idempotent.</summary>
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
}
