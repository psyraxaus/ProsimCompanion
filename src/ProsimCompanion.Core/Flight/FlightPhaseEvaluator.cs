using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Core.Flight;

/// <summary>
/// Pure phase derivation over the <see cref="FlightPhaseRules"/> table: (sample, current
/// phase, options) → the first matching rule's decision, or null to hold. No timers, no
/// state beyond the passed-in current phase — fully unit-testable. Debounce is applied by
/// the engine, not here; the decision only carries the hold the matched rule asks for.
/// </summary>
public static class FlightPhaseEvaluator
{
    /// <summary>Derives the target phase with the compiled-in defaults. Invalid snapshots and
    /// holds return the current phase — the pre-table contract every consumer test drives.</summary>
    public static FlightPhase Evaluate(FlightDataSnapshot snapshot, FlightPhase current)
        => Decide(snapshot, current, FlightStateOptions.Default)?.Target ?? current;

    /// <summary>Evaluates the rule table. Returns null when the sample is invalid or a hold
    /// rule matched (or the matched rule targets the current phase).</summary>
    public static PhaseDecision? Decide(FlightDataSnapshot snapshot, FlightPhase current, FlightStateOptions options)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(options);

        if (!snapshot.IsValid)
        {
            return null;
        }

        var rules = snapshot.OnGround ? FlightPhaseRules.Ground : FlightPhaseRules.Airborne;
        foreach (var rule in rules)
        {
            if (!rule.AppliesFrom(current) || !rule.When(current, snapshot, options))
            {
                continue;
            }

            if (rule.To is not { } target || target == current)
            {
                return null;
            }

            return new PhaseDecision(target, rule.Id, rule.Reason(current, snapshot), rule.Debounce(current, options));
        }

        return null;
    }
}
