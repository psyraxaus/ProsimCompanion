namespace ProsimCompanion.Core.State;

/// <summary>
/// Everything a caller that does not care WHICH question must know about a captain's-question
/// store: the FO dialogue (voices whatever is open), the web card (buttons for whatever is
/// open), the TTL sweeper and the command handlers. The typed adapters add their raisers.
/// </summary>
public interface ICaptainsQuestionStore
{
    /// <summary>The question's fixed rules.</summary>
    CaptainsQuestionDefinition Definition { get; }

    /// <summary>The current question state (the question part of the store's snapshot).</summary>
    CaptainsQuestionState Asking { get; }

    /// <summary>Raised once per ask and once per re-ask — the Speech pillar voices it.</summary>
    event EventHandler<CaptainsQuestion>? QuestionRaised;

    /// <summary>Raised when an answer settles the question: true = yes / picked. The source
    /// names who answered ("voice", "web", "called", "timeout", "default at taxi-in").</summary>
    event EventHandler<(bool Accepted, string Source)>? Answered;

    /// <summary>Raised on every snapshot change (the web card re-reads <see cref="Asking"/>).</summary>
    event Action? Changed;

    /// <summary>Yes. True when it changed the state.</summary>
    bool Accept(string source);

    /// <summary>No, with the reason the decision log records. True when it changed the state.</summary>
    bool Decline(string source, string reason);

    /// <summary>Picks one of the standing question's choices by label or token. False when no
    /// question stands or nothing matches.</summary>
    bool Choose(string labelOrToken, string source);

    /// <summary>Withdraws a standing question without an answer.</summary>
    void Withdraw();

    /// <summary>Settles a question whose TTL passed, per the definition's expiry rule.</summary>
    CaptainsQuestionExpiry ExpireOverdue(DateTimeOffset nowUtc);
}

/// <summary>
/// The one captain's-question store (architecture review C2, 2026-10-19). Four store +
/// dialogue pairs — de-icing, the turnaround start, the arrival-gate suffix, the pushback
/// direction — shared one skeleton (ask once, accept/decline, raise/answer events, reset with
/// the flight cycle) and differed by accident: only de-icing had a TTL, a re-ask and the
/// "silence is not an answer" rule (issue #166, EFHK 2026-10-10: 32 s of silence declined the
/// question and GSX's own offer was then accepted against that decline). This base owns the
/// skeleton once, parameterised by a <see cref="CaptainsQuestionDefinition"/>, so the #166
/// rules hold for every question; each typed adapter keeps only its raiser and its extra facts.
/// <para>Events are edges of the state: one <see cref="Commit"/> runs one update and raises
/// <see cref="QuestionRaised"/> when a new or re-asked question appears and
/// <see cref="Answered"/> when an answer appears — so an adapter that changes its own facts
/// and the question in the same update (the de-icing verdict, the pushback choice) notifies
/// its observers once, never twice (the GSX menu catalogue acts on the first notification).</para>
/// </summary>
public abstract class CaptainsQuestionStore<TSnapshot> : SnapshotStore<TSnapshot>, ICaptainsQuestionStore, IDisposable
    where TSnapshot : ICaptainsQuestionSnapshot<TSnapshot>
{
    private readonly GroundOpsSignals _signals;
    private readonly TSnapshot _empty;
    private readonly IDisposable _changedRelay;

    protected CaptainsQuestionStore(CaptainsQuestionDefinition definition, TSnapshot empty, GroundOpsSignals signals)
        : base(empty)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(empty);
        ArgumentNullException.ThrowIfNull(signals);
        Definition = definition;
        _empty = empty;
        _signals = signals;
        _signals.FlightCycleReset += Reset;
        _changedRelay = Observe(_ => Changed?.Invoke());
    }

    /// <inheritdoc />
    public CaptainsQuestionDefinition Definition { get; }

    /// <inheritdoc />
    public CaptainsQuestionState Asking => Snapshot().Asking;

    /// <inheritdoc />
    public event EventHandler<CaptainsQuestion>? QuestionRaised;

    /// <inheritdoc />
    public event EventHandler<(bool Accepted, string Source)>? Answered;

    /// <inheritdoc />
    public event Action? Changed;

    /// <summary>The flight-cycle reset: everything back to empty (the adapters' extra facts too).</summary>
    public void Reset() => Update(_ => _empty);

    public virtual void Dispose()
    {
        _signals.FlightCycleReset -= Reset;
        _changedRelay.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    public bool Accept(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        return Commit(s => s.WithAsking(CaptainsQuestionRules.Accept(s.Asking, source, DateTimeOffset.UtcNow))).Answered is not null;
    }

    /// <inheritdoc />
    public bool Decline(string source, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return Commit(s => s.WithAsking(CaptainsQuestionRules.Decline(s.Asking, source, reason, DateTimeOffset.UtcNow))).Answered is not null;
    }

    /// <inheritdoc />
    public virtual bool Choose(string labelOrToken, string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(labelOrToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        QuestionChoice? chosen = null;
        Commit(s =>
        {
            var (next, pick) = CaptainsQuestionRules.Choose(s.Asking, labelOrToken, source, DateTimeOffset.UtcNow);
            chosen = pick;
            return s.WithAsking(next);
        });
        return chosen is not null;
    }

    /// <inheritdoc />
    public void Withdraw() => Commit(s => s.WithAsking(CaptainsQuestionRules.Withdraw(s.Asking)));

    /// <inheritdoc />
    public CaptainsQuestionExpiry ExpireOverdue(DateTimeOffset nowUtc)
    {
        var outcome = CaptainsQuestionExpiry.Never;
        Commit(s =>
        {
            var (next, result) = CaptainsQuestionRules.Expire(s.Asking, Definition, nowUtc);
            outcome = result;
            return s.WithAsking(next);
        });
        return outcome;
    }

    /// <summary>Asks the standing question again (one re-ask, fresh TTL); the question as
    /// re-raised, or null when the rules refused.</summary>
    public CaptainsQuestion? ReAsk()
        => Commit(s => s.WithAsking(CaptainsQuestionRules.ReAsk(s.Asking, Definition, DateTimeOffset.UtcNow))).Raised;

    /// <summary>Raises the question (the adapters' typed <c>Ask</c> methods call this); the
    /// question as raised, or null when the rules refused (one stands, or the cycle is answered).</summary>
    protected CaptainsQuestion? Raise(string prompt, IReadOnlyList<QuestionChoice>? choices = null, string? subject = null, TimeSpan? ttl = null)
        => Commit(s => s.WithAsking(CaptainsQuestionRules.Ask(s.Asking, Definition, prompt, choices ?? [], subject, ttl, DateTimeOffset.UtcNow))).Raised;

    /// <summary>What one <see cref="Commit"/> changed: the question it raised and/or the answer
    /// it recorded (null for neither).</summary>
    protected readonly record struct CommitOutcome(CaptainsQuestion? Raised, CaptainsAnswer? Answered);

    /// <summary>
    /// Runs one update and raises the events its edges imply. <paramref name="announceAnswer"/>
    /// false records an answer without the <see cref="Answered"/> edge — the de-icing policy's
    /// own <c>auto</c> verdict is a decision, not the captain's answer, and the live event
    /// stream has no <c>gsx-deice-policy</c> line for it (2026-10-09).
    /// </summary>
    protected CommitOutcome Commit(Func<TSnapshot, TSnapshot> mutate, bool announceAnswer = true)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        CaptainsQuestionState? before = null;
        CaptainsQuestionState? after = null;
        Update(s =>
        {
            before = s.Asking;
            var next = mutate(s);
            after = next.Asking;
            return next;
        });

        var raised = after!.Question is { } question && !ReferenceEquals(question, before!.Question) ? question : null;
        var answered = after.Answer is { } answer && !ReferenceEquals(answer, before!.Answer) ? answer : null;
        if (raised is not null)
        {
            QuestionRaised?.Invoke(this, raised);
        }

        if (answered is not null && announceAnswer)
        {
            Answered?.Invoke(this, (answered.Accepted, answered.Source));
        }

        return new CommitOutcome(raised, answered);
    }
}
