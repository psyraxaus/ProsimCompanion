namespace ProsimCompanion.Core.State;

/// <summary>
/// Per-flight arrival-gate disambiguation (2026-10-11, issue #156). When <c>gate.select</c>
/// answers <c>ambiguous</c> with stands that differ from the request only by a trailing letter
/// (W36 → " Gate 36A" / " Gate 36B") and nothing else decides, the GSX pillar asks here; the
/// Speech pillar voices it ("W36 is 36A or 36B in this scenery — which?"), the OFP page and the
/// Status card show buttons, and the answer — from any of them — sends the chosen stand.
/// Resets with the flight cycle.
/// <para>Adapter over <see cref="CaptainsQuestionStore{TSnapshot}"/> (architecture review C2):
/// a pick-one question whose subject is the gate as the pilot named it — the same gate asked
/// twice is one question, another gate replaces it. No TTL: probe <c>gsx-gate-suffix</c>
/// asserts no default before the TaxiIn edge, so the GSX pillar settles it itself with the
/// first variant at taxi-in (<c>Choose(token, "default at taxi-in")</c>) and a clock never
/// picks for the pilot. The choice's token is GSX's bglName ("Gate W 36A") for <c>gate.select</c>.</para>
/// </summary>
public sealed class GateChoiceStore : CaptainsQuestionStore<CaptainsQuestionState>
{
    /// <summary>Pick one, per gate, stands until settled.</summary>
    public static CaptainsQuestionDefinition QuestionDefinition { get; } = new(
        "gateChoice",
        CaptainsQuestionShape.PickOne,
        DefaultTtl: null,
        CaptainsQuestionExpiry.Never,
        CaptainsQuestionDefinition.StandardMaxAsks,
        AnswerIsFinalForCycle: false);

    public GateChoiceStore(GroundOpsSignals signals)
        : base(QuestionDefinition, CaptainsQuestionState.Empty, signals)
    {
    }

    /// <summary>Asks the pilot. Idempotent while a question for the same gate is open; a
    /// question for another gate replaces the old one.</summary>
    /// <param name="gate">The gate as the pilot/ATC named it ("W36").</param>
    /// <param name="candidates">The variants GSX offered, in GSX's order (the first is the
    /// default the GSX pillar takes at taxi-in when nobody answers).</param>
    /// <param name="prompt">The line the FO speaks and the web cards print.</param>
    public void Ask(string gate, IReadOnlyList<QuestionChoice> candidates, string prompt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gate);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        if (candidates.Count == 0)
        {
            throw new ArgumentException("at least one candidate is required", nameof(candidates));
        }

        Raise(prompt, candidates, subject: gate);
    }
}
