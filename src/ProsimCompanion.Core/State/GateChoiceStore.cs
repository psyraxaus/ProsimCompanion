namespace ProsimCompanion.Core.State;

/// <summary>One stand the pilot can pick between when GSX's scenery splits the requested gate
/// into suffix variants (issue #156, EFHK W36 → 36A / 36B).</summary>
/// <param name="Label">What the pilot says or clicks: "36A".</param>
/// <param name="Token">The identity to send to <c>gate.select</c> for it (GSX's bglName, "Gate W 36A").</param>
/// <param name="Detail">A hint for the buttons and the FO ("jetway", "Safedock"); null when none.</param>
public sealed record GateCandidate(string Label, string Token, string? Detail);

/// <summary>An open "which one?" question for the arrival gate.</summary>
/// <param name="AskedAtUtc">When it was raised.</param>
/// <param name="Gate">The gate as the pilot/ATC named it ("W36").</param>
/// <param name="Candidates">The variants GSX offered, in GSX's order (the first is the default
/// the GSX pillar takes at taxi-in when nobody answers).</param>
/// <param name="Prompt">The line the FO speaks and the web cards print.</param>
public sealed record GateChoiceQuestion(DateTimeOffset AskedAtUtc, string Gate, IReadOnlyList<GateCandidate> Candidates, string Prompt);

/// <summary>The pilot's (or the default's) answer to a <see cref="GateChoiceQuestion"/>.</summary>
public sealed record GateChoice(string Gate, GateCandidate Candidate, string Source, DateTimeOffset AtUtc);

public sealed record GateChoiceSnapshot(GateChoiceQuestion? Question, GateChoice? Choice)
{
    public static GateChoiceSnapshot Empty { get; } = new(null, null);
}

/// <summary>
/// Per-flight arrival-gate disambiguation (2026-10-11, issue #156). When <c>gate.select</c>
/// answers <c>ambiguous</c> with stands that differ from the request only by a trailing letter
/// (W36 → " Gate 36A" / " Gate 36B") and nothing else decides, the GSX pillar asks here; the
/// Speech pillar voices it ("W36 is 36A or 36B in this scenery — which?"), the OFP page and the
/// Status card show buttons, and the answer — from any of them — sends the chosen stand.
/// Resets with the flight cycle. Modelled on <see cref="PushbackChoiceStore"/>.
/// </summary>
public sealed class GateChoiceStore : SnapshotStore<GateChoiceSnapshot>, IDisposable
{
    private readonly GroundOpsSignals _signals;

    public GateChoiceStore(GroundOpsSignals signals)
        : base(GateChoiceSnapshot.Empty)
    {
        ArgumentNullException.ThrowIfNull(signals);
        _signals = signals;
        _signals.FlightCycleReset += Reset;
    }

    /// <summary>Raised once per question (the FO dialogue listens).</summary>
    public event EventHandler<GateChoiceQuestion>? QuestionRaised;

    public void Dispose() => _signals.FlightCycleReset -= Reset;

    /// <summary>Asks the pilot. Idempotent while a question for the same gate is open; a
    /// question for another gate replaces the old one.</summary>
    public void Ask(string gate, IReadOnlyList<GateCandidate> candidates, string prompt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gate);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        if (candidates.Count == 0)
        {
            throw new ArgumentException("at least one candidate is required", nameof(candidates));
        }

        GateChoiceQuestion? raised = null;
        Update(s =>
        {
            if (s.Question is { } open && string.Equals(open.Gate, gate, StringComparison.OrdinalIgnoreCase))
            {
                return s;
            }

            raised = new GateChoiceQuestion(DateTimeOffset.UtcNow, gate, candidates, prompt);
            return s with { Question = raised, Choice = null };
        });

        if (raised is not null)
        {
            QuestionRaised?.Invoke(this, raised);
        }
    }

    /// <summary>Answers the open question with the candidate whose label or token matches
    /// (case-insensitive, whitespace ignored). False when no question is open or nothing
    /// matches — the caller reads back the misunderstanding instead of guessing.</summary>
    public bool Choose(string labelOrToken, string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(labelOrToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        var wanted = Normalize(labelOrToken);
        var chosen = false;
        Update(s =>
        {
            if (s.Question is not { } question)
            {
                return s;
            }

            var candidate = question.Candidates.FirstOrDefault(c => Normalize(c.Label) == wanted || Normalize(c.Token) == wanted);
            if (candidate is null)
            {
                return s;
            }

            chosen = true;
            return s with { Question = null, Choice = new GateChoice(question.Gate, candidate, source, DateTimeOffset.UtcNow) };
        });

        return chosen;
    }

    /// <summary>Withdraws an open question (GSX settled it itself, the request was cancelled).</summary>
    public void ClearQuestion() => Update(s => s.Question is null ? s : s with { Question = null });

    public void Reset() => Update(_ => GateChoiceSnapshot.Empty);

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
