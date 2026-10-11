namespace ProsimCompanion.Core.State;

/// <summary>An open "commence ground services?" question for the captain on a turnaround
/// (issue #169, <c>gsx.turnaroundAskFirst</c>). The FO voices it; the departure sequence
/// waits for the answer, the usual start triggers ("commence ground services", the Start
/// button) answer it implicitly.</summary>
public sealed record TurnaroundStartQuestion(DateTimeOffset AskedAtUtc, string Prompt);

/// <summary>The turnaround start question's state for the current departure cycle.</summary>
/// <param name="Question">The open question, null when none stands.</param>
/// <param name="Accepted">True once the captain said yes (or the sequence started another way).</param>
/// <param name="Declined">The reason the question was declined, null while not declined.</param>
public sealed record TurnaroundStartSnapshot(
    TurnaroundStartQuestion? Question,
    bool Accepted,
    string? Declined)
{
    public static TurnaroundStartSnapshot Empty { get; } = new(null, false, null);

    /// <summary>True while a question stands and nobody has answered it.</summary>
    public bool QuestionOpen => Question is not null && !Accepted && Declined is null;
}

/// <summary>
/// Per-turnaround "commence ground services?" question (issue #169). Raised by the GSX
/// automation when <c>gsx.groundPrepActivation = voiceThenAuto</c> with
/// <c>gsx.turnaroundAskFirst</c> on and the next leg's plan has settled at the stand; answered
/// by the FO voice dialogue (yes/no) or implicitly by any other start of the sequence. Resets
/// with the flight cycle. Kept in Core so Speech and Gsx share it without referencing each
/// other (feature projects never reference each other).
/// </summary>
public sealed class TurnaroundStartStore : SnapshotStore<TurnaroundStartSnapshot>, IDisposable
{
    private readonly GroundOpsSignals _signals;

    public TurnaroundStartStore(GroundOpsSignals signals)
        : base(TurnaroundStartSnapshot.Empty)
    {
        ArgumentNullException.ThrowIfNull(signals);
        _signals = signals;
        _signals.FlightCycleReset += Reset;
    }

    /// <summary>Raised once per question — the Speech pillar voices it.</summary>
    public event EventHandler<TurnaroundStartQuestion>? QuestionRaised;

    /// <summary>Raised when the captain's answer settles the question: true = commence. The
    /// argument names who answered ("voice", "web", "timeout", …).</summary>
    public event EventHandler<(bool Accepted, string Source)>? Answered;

    public void Dispose() => _signals.FlightCycleReset -= Reset;

    /// <summary>Raises the question once per cycle; a second call while one stands (or after
    /// an answer) is a no-op.</summary>
    public bool Ask(string prompt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        TurnaroundStartQuestion? raised = null;
        Update(s =>
        {
            if (s.Question is not null || s.Accepted || s.Declined is not null)
            {
                return s;
            }

            raised = new TurnaroundStartQuestion(DateTimeOffset.UtcNow, prompt);
            return s with { Question = raised };
        });

        if (raised is null)
        {
            return false;
        }

        QuestionRaised?.Invoke(this, raised);
        return true;
    }

    /// <summary>The captain wants the services (voice "yes", or the sequence started by any
    /// other trigger). Idempotent.</summary>
    public void Accept(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        var changed = false;
        Update(s =>
        {
            if (s.Accepted)
            {
                return s;
            }

            changed = true;
            return s with { Accepted = true, Declined = null };
        });

        if (changed)
        {
            Answered?.Invoke(this, (true, source));
        }
    }

    /// <summary>The captain said no (or nobody answered): the turnaround stays on the voice
    /// gate — "commence ground services" / the Start button still work. Idempotent.</summary>
    public void Decline(string source, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var changed = false;
        Update(s =>
        {
            if (s.Accepted || s.Declined is not null)
            {
                return s;
            }

            changed = true;
            return s with { Declined = reason };
        });

        if (changed)
        {
            Answered?.Invoke(this, (false, source));
        }
    }

    public void Reset() => Update(_ => TurnaroundStartSnapshot.Empty);
}
