using ProsimCompanion.Core.Fuel;

namespace ProsimCompanion.Core.State;

/// <summary>The latest holding-time estimate (issue #183), written by the fuel check on every
/// check and read by the Fuel page, the Flight Status hero and the fact sheet. Null until the
/// first check of the flight; cleared with the flight cycle.</summary>
public sealed class HoldingTimeStore : SnapshotStore<HoldingEstimate?>, IDisposable
{
    private readonly GroundOpsSignals _signals;

    public HoldingTimeStore(GroundOpsSignals signals)
        : base(null)
    {
        ArgumentNullException.ThrowIfNull(signals);
        _signals = signals;
        _signals.FlightCycleReset += Clear;
    }

    public HoldingEstimate? Current => Snapshot();

    public void Set(HoldingEstimate estimate)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        Update(_ => estimate);
    }

    public void Clear() => Update(_ => null);

    public void Dispose()
    {
        _signals.FlightCycleReset -= Clear;
        GC.SuppressFinalize(this);
    }
}

/// <summary>The fuel-decision question's state: the question, the advice behind it and what
/// the captain's yes added to the block fuel.</summary>
/// <param name="Asking">The shared question part.</param>
/// <param name="Advice">The advisor's last decision this cycle; null before one was made.</param>
/// <param name="AppliedExtraKg">The extra the INIT override was raised by; null until applied.</param>
public sealed record FuelDecisionSnapshot(CaptainsQuestionState Asking, FuelDecision? Advice, double? AppliedExtraKg)
    : ICaptainsQuestionSnapshot<FuelDecisionSnapshot>
{
    public static FuelDecisionSnapshot Empty { get; } = new(CaptainsQuestionState.Empty, null, null);

    FuelDecisionSnapshot ICaptainsQuestionSnapshot<FuelDecisionSnapshot>.WithAsking(CaptainsQuestionState asking)
        => this with { Asking = asking };
}

/// <summary>
/// "Recommend twenty minutes holding, seven hundred kilos — take it?" (issue #183). Adapter
/// over <see cref="CaptainsQuestionStore{TSnapshot}"/>: the fuel-decision service raises it
/// after the OFP import when the advisor recommends extra; yes raises the INIT block-fuel
/// override, no leaves the plan. #166 rules: 5-minute TTL counts as no, one re-ask. Once per
/// cycle: a second OFP in the same turnaround does not ask again.
/// </summary>
public sealed class FuelDecisionStore : CaptainsQuestionStore<FuelDecisionSnapshot>
{
    public static CaptainsQuestionDefinition QuestionDefinition { get; } = new(
        "fuelDecision",
        CaptainsQuestionShape.YesNo,
        CaptainsQuestionDefinition.StandardTtl,
        CaptainsQuestionExpiry.CountsAsNo,
        CaptainsQuestionDefinition.StandardMaxAsks,
        AnswerIsFinalForCycle: true,
        TimeoutReasonSuffix: " — plan fuel stands");

    public FuelDecisionStore(GroundOpsSignals signals)
        : base(QuestionDefinition, FuelDecisionSnapshot.Empty, signals)
    {
    }

    /// <summary>The advisor's last decision this cycle.</summary>
    public FuelDecision? Advice => Snapshot().Advice;

    /// <summary>Records the advice (whatever it recommends) — the Fuel page shows it.</summary>
    public void SetAdvice(FuelDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        Update(s => s with { Advice = decision });
    }

    /// <summary>Raises the question with the prompt; false while one stands or the cycle is answered.</summary>
    public bool Ask(string prompt, FuelDecision decision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentNullException.ThrowIfNull(decision);
        return Commit(s => s with { Advice = decision, Asking = CaptainsQuestionRules.Ask(s.Asking, Definition, prompt, [], null, null, DateTimeOffset.UtcNow) }).Raised is not null;
    }

    /// <summary>The captain's extra was applied to the block fuel (by the question's yes or by
    /// "extra fuel …" directly); also settles an open question as yes.</summary>
    public void MarkApplied(double extraKg, string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        Commit(s => s with
        {
            AppliedExtraKg = extraKg,
            Asking = s.Asking.Question is null ? s.Asking : CaptainsQuestionRules.Accept(s.Asking, source, DateTimeOffset.UtcNow),
        }, announceAnswer: false);
    }
}
