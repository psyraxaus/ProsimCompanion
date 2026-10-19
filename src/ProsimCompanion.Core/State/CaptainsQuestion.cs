namespace ProsimCompanion.Core.State;

/// <summary>The answer shape of a captain's question.</summary>
public enum CaptainsQuestionShape
{
    /// <summary>"Request de-icing?" — the captain says yes or no.</summary>
    YesNo,

    /// <summary>"36A or 36B?" — the captain picks one labelled choice.</summary>
    PickOne,
}

/// <summary>What the TTL does to a question nobody answered.</summary>
public enum CaptainsQuestionExpiry
{
    /// <summary>The question never expires on its own; the raiser settles it (the arrival
    /// gate: the first variant goes out at taxi-in — probe <c>gsx-gate-suffix</c> asserts no
    /// default before the TaxiIn edge, so a clock must not pick for the pilot).</summary>
    Never,

    /// <summary>A yes/no question nobody answered counts as no (source <c>timeout</c>). Issue
    /// #166: the ONLY path that declines on silence — the FO's listen window never does.</summary>
    CountsAsNo,

    /// <summary>A pick-one question nobody answered is withdrawn — the raiser's default path
    /// decides (GSX's own menu stays open for the pilot).</summary>
    Withdraws,
}

/// <summary>
/// The fixed shape of one captain's question — one per store. Everything a raiser, a voice
/// table, a web card or a command handler must know about a question's rules sits here, so a
/// new question is a definition, not a fifth store.
/// </summary>
/// <param name="Topic">The stable id the phrase tables, the web card and the logs use
/// ("deice", "turnaroundStart", "gateChoice", "pushback").</param>
/// <param name="Shape">Yes/no or pick one of N.</param>
/// <param name="DefaultTtl">How long a question stands before <paramref name="Expiry"/>
/// applies; null = stands until the raiser settles it.</param>
/// <param name="Expiry">What the TTL does.</param>
/// <param name="MaxAsks">The first ask plus the re-asks a raiser may request (issue #166: the
/// de-icing question is re-asked once when its step's turn comes — never a third time).</param>
/// <param name="AnswerIsFinalForCycle">True when an answer settles the matter for the whole
/// ground-ops cycle (de-icing, the turnaround start): a later ask is a no-op until the
/// flight-cycle reset. False when a new question may follow an answer (the next gate, the
/// next pushback menu).</param>
/// <param name="TimeoutReasonSuffix">Appended to the "no answer within N minutes" decline
/// reason (the de-icing probe expects the setting name after it).</param>
public sealed record CaptainsQuestionDefinition(
    string Topic,
    CaptainsQuestionShape Shape,
    TimeSpan? DefaultTtl,
    CaptainsQuestionExpiry Expiry,
    int MaxAsks,
    bool AnswerIsFinalForCycle,
    string? TimeoutReasonSuffix = null)
{
    /// <summary>The #166 figure (<c>gsx.deice.questionTimeoutMinutes</c> default): an
    /// unanswered question past it is settled, so a departure with nobody at the FO's mic or
    /// the web page never waits forever (degrade, not fail).</summary>
    public static TimeSpan StandardTtl { get; } = TimeSpan.FromMinutes(5);

    /// <summary>One ask and one re-ask (#166).</summary>
    public const int StandardMaxAsks = 2;
}

/// <summary>One labelled choice of a pick-one question.</summary>
/// <param name="Label">What the pilot says or clicks: "36A".</param>
/// <param name="Token">The identity the raiser acts on for it (GSX's bglName "Gate W 36A",
/// the pushback menu line); equals the label when there is no separate identity.</param>
/// <param name="Detail">A hint for the buttons and the FO ("jetway", "Safedock"); null when none.</param>
public sealed record QuestionChoice(string Label, string Token, string? Detail);

/// <summary>An open question for the captain. The FO voices it, the web cards show it with
/// buttons, the raiser holds until it is answered, withdrawn or expired.</summary>
/// <param name="Topic">The definition's topic.</param>
/// <param name="AskedAtUtc">When this attempt was asked (the TTL counts from here — a re-ask
/// restarts it).</param>
/// <param name="Prompt">The line the FO speaks (its table may re-render it) and the web prints.</param>
/// <param name="Ttl">How long this attempt stands; null = until settled.</param>
/// <param name="Attempt">1 for the first ask, 2 for the one re-ask.</param>
/// <param name="Choices">The pick-one choices in the raiser's order (the first is the raiser's
/// default where it has one); empty for a yes/no question.</param>
/// <param name="Subject">What the question is about when a raiser may ask about several
/// things per cycle (the gate as the pilot named it, "W36"); null otherwise.</param>
public sealed record CaptainsQuestion(
    string Topic,
    DateTimeOffset AskedAtUtc,
    string Prompt,
    TimeSpan? Ttl,
    int Attempt,
    IReadOnlyList<QuestionChoice> Choices,
    string? Subject)
{
    /// <summary>When this attempt expires; null when it stands until settled.</summary>
    public DateTimeOffset? ExpiresAtUtc => Ttl is { } ttl ? AskedAtUtc + ttl : null;

    /// <summary>True while the attempt's TTL has not passed (always true without a TTL).</summary>
    public bool OpenAt(DateTimeOffset nowUtc) => Ttl is not { } ttl || nowUtc - AskedAtUtc < ttl;
}

/// <summary>The captain's settled answer (or its absence settled by the clock).</summary>
/// <param name="Accepted">True = yes / a choice was made; false = declined.</param>
/// <param name="Chosen">The pick for a pick-one question; null for yes/no.</param>
/// <param name="Source">Who answered: "voice", "web", "called", "timeout", "default at taxi-in", "policy".</param>
/// <param name="Reason">The decline reason as the decision log records it; null on yes.</param>
/// <param name="AtUtc">When.</param>
/// <param name="Subject">The question's subject the answer settled.</param>
public sealed record CaptainsAnswer(
    bool Accepted,
    QuestionChoice? Chosen,
    string Source,
    string? Reason,
    DateTimeOffset AtUtc,
    string? Subject);

/// <summary>
/// The question state every captain's-question store carries: the open question (null when
/// none stands — answered, withdrawn, expired or never asked) and the answer this cycle (null
/// while unanswered). A store whose snapshot is nothing but the question uses this record
/// as its snapshot; a richer store embeds it (see <see cref="ICaptainsQuestionSnapshot{TSelf}"/>).
/// </summary>
public sealed record CaptainsQuestionState(CaptainsQuestion? Question, CaptainsAnswer? Answer)
    : ICaptainsQuestionSnapshot<CaptainsQuestionState>
{
    public static CaptainsQuestionState Empty { get; } = new(null, null);

    /// <summary>True once the captain said yes (or picked), this cycle.</summary>
    public bool Accepted => Answer is { Accepted: true };

    /// <summary>The decline reason; null while not declined.</summary>
    public string? Declined => Answer is { Accepted: false } answer ? answer.Reason : null;

    /// <summary>The pick, for a pick-one question that was answered.</summary>
    public QuestionChoice? Chosen => Answer?.Chosen;

    /// <summary>True while a question stands, whatever the clock says.</summary>
    public bool QuestionStands => Question is not null;

    /// <summary>True while a question stands and its TTL has not passed.</summary>
    public bool QuestionOpen(DateTimeOffset nowUtc) => Question is { } question && question.OpenAt(nowUtc);

    CaptainsQuestionState ICaptainsQuestionSnapshot<CaptainsQuestionState>.Asking => this;

    CaptainsQuestionState ICaptainsQuestionSnapshot<CaptainsQuestionState>.WithAsking(CaptainsQuestionState asking) => asking;
}

/// <summary>
/// A store snapshot that carries a <see cref="CaptainsQuestionState"/>: the seam through which
/// <see cref="CaptainsQuestionStore{TSnapshot}"/> reads and writes the question part of a
/// richer snapshot (the de-icing verdict, the pushback suggestion) without knowing the rest.
/// </summary>
public interface ICaptainsQuestionSnapshot<TSelf>
    where TSelf : ICaptainsQuestionSnapshot<TSelf>
{
    /// <summary>The question part of this snapshot.</summary>
    CaptainsQuestionState Asking { get; }

    /// <summary>This snapshot with the question part replaced.</summary>
    TSelf WithAsking(CaptainsQuestionState asking);
}

/// <summary>
/// The pure rules of a captain's question — once per cycle, same-subject idempotence,
/// re-ask attempts, yes overrides no, the TTL verdict — as functions from state to state. The
/// store commits them; the tests table them. No clock, no events: the store derives the
/// Raised/Answered edges from the state before and after.
/// </summary>
public static class CaptainsQuestionRules
{
    /// <summary>Raises a question. A no-op while one for the same subject stands, and — for a
    /// definition whose answer is final for the cycle — once the cycle has an answer. A
    /// question for another subject replaces the standing one (the pilot renamed the gate).</summary>
    public static CaptainsQuestionState Ask(
        CaptainsQuestionState state,
        CaptainsQuestionDefinition definition,
        string prompt,
        IReadOnlyList<QuestionChoice> choices,
        string? subject,
        TimeSpan? ttl,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentNullException.ThrowIfNull(choices);
        if (definition.Shape == CaptainsQuestionShape.PickOne && choices.Count == 0)
        {
            throw new ArgumentException("a pick-one question needs at least one choice", nameof(choices));
        }

        if (definition.AnswerIsFinalForCycle && state.Answer is not null)
        {
            return state;
        }

        if (state.Question is { } open && string.Equals(open.Subject, subject, StringComparison.OrdinalIgnoreCase))
        {
            return state;
        }

        var question = new CaptainsQuestion(definition.Topic, nowUtc, prompt, ttl ?? definition.DefaultTtl, 1, choices, subject);
        return state with { Question = question, Answer = null };
    }

    /// <summary>Asks the standing question again with a fresh TTL (issue #166: the de-icing
    /// step's turn has come and the first ask may be minutes old). A no-op when nothing
    /// stands, the attempt has expired, or <see cref="CaptainsQuestionDefinition.MaxAsks"/>
    /// is reached — never a third ask.</summary>
    public static CaptainsQuestionState ReAsk(CaptainsQuestionState state, CaptainsQuestionDefinition definition, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(definition);
        if (state.Question is not { } question || !question.OpenAt(nowUtc) || question.Attempt >= definition.MaxAsks)
        {
            return state;
        }

        return state with { Question = question with { AskedAtUtc = nowUtc, Attempt = question.Attempt + 1 } };
    }

    /// <summary>The captain said yes — by voice, button, command, or by doing the thing (the
    /// Start button before the FO asked; a de-ice called on GSX's menu). Idempotent; a yes
    /// after a no replaces it (the captain changed their mind before the truck came).</summary>
    public static CaptainsQuestionState Accept(CaptainsQuestionState state, string source, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (state.Accepted)
        {
            return state;
        }

        return state with { Question = null, Answer = new CaptainsAnswer(true, null, source, null, nowUtc, state.Question?.Subject) };
    }

    /// <summary>The captain said no (or the clock did, source <c>timeout</c>). Idempotent; a no
    /// never overrides a yes.</summary>
    public static CaptainsQuestionState Decline(CaptainsQuestionState state, string source, string reason, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (state.Answer is not null)
        {
            return state;
        }

        return state with { Question = null, Answer = new CaptainsAnswer(false, null, source, reason, nowUtc, state.Question?.Subject) };
    }

    /// <summary>Answers a standing pick-one question with the choice whose label or token
    /// matches (case-insensitive, only letters and digits compared). Unchanged state and a
    /// null pick when no question stands or nothing matches — the caller reads back the
    /// misunderstanding instead of guessing.</summary>
    public static (CaptainsQuestionState State, QuestionChoice? Chosen) Choose(
        CaptainsQuestionState state, string labelOrToken, string source, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(labelOrToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (state.Question is not { } question)
        {
            return (state, null);
        }

        var wanted = Normalize(labelOrToken);
        var chosen = question.Choices.FirstOrDefault(c => Normalize(c.Label) == wanted || Normalize(c.Token) == wanted);
        if (chosen is null)
        {
            return (state, null);
        }

        return (state with { Question = null, Answer = new CaptainsAnswer(true, chosen, source, null, nowUtc, question.Subject) }, chosen);
    }

    /// <summary>Withdraws a standing question without an answer (GSX settled it itself, the
    /// menu is gone, the request was cancelled).</summary>
    public static CaptainsQuestionState Withdraw(CaptainsQuestionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Question is null ? state : state with { Question = null };
    }

    /// <summary>Applies the definition's expiry to a question whose TTL has passed; returns
    /// what happened (<see cref="CaptainsQuestionExpiry.Never"/> = nothing to do).</summary>
    public static (CaptainsQuestionState State, CaptainsQuestionExpiry Outcome) Expire(
        CaptainsQuestionState state, CaptainsQuestionDefinition definition, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(definition);
        if (state.Question is not { Ttl: { } ttl } question || question.OpenAt(nowUtc))
        {
            return (state, CaptainsQuestionExpiry.Never);
        }

        return definition.Expiry switch
        {
            CaptainsQuestionExpiry.CountsAsNo => (
                Decline(state, "timeout", $"no answer within {ttl.TotalMinutes:0} minutes{definition.TimeoutReasonSuffix}", nowUtc),
                CaptainsQuestionExpiry.CountsAsNo),
            CaptainsQuestionExpiry.Withdraws => (Withdraw(state), CaptainsQuestionExpiry.Withdraws),
            _ => (state, CaptainsQuestionExpiry.Never),
        };
    }

    private static string Normalize(string value)
    {
        Span<char> buffer = stackalloc char[value.Length];
        var n = 0;
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                buffer[n++] = char.ToUpperInvariant(c);
            }
        }

        return new string(buffer[..n]);
    }
}
