using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Core.Flight;

/// <summary>
/// One edge of the flight-phase graph. Rules are evaluated in table order and the first
/// match wins; a rule whose <see cref="To"/> is null (or equals the current phase) is a
/// <b>hold</b> — it stops evaluation without a transition, which is how "stay in the roll
/// while IAS is high even if thrust reads unset" is expressed without nested ifs.
/// </summary>
/// <param name="Id">Stable kebab-case identifier; recorded on every commit so a probe or a
/// replay diff can name the rule that fired.</param>
/// <param name="From">Phases this rule may fire from; null = any phase.</param>
/// <param name="To">Target phase; null = hold the current phase.</param>
/// <param name="When">The evidence test: (current phase, sample, options).</param>
/// <param name="Debounce">How long the evidence must persist before the engine commits,
/// given the phase being left.</param>
/// <param name="Reason">Human-readable justification built from the sample — logged and
/// carried on the phase-changed event.</param>
public sealed record PhaseRule(
    string Id,
    IReadOnlySet<FlightPhase>? From,
    FlightPhase? To,
    Func<FlightPhase, FlightDataSnapshot, FlightStateOptions, bool> When,
    Func<FlightPhase, FlightStateOptions, TimeSpan> Debounce,
    Func<FlightPhase, FlightDataSnapshot, string> Reason)
{
    /// <summary>True when the rule may fire from <paramref name="current"/>.</summary>
    public bool AppliesFrom(FlightPhase current) => From is null || From.Contains(current);
}

/// <summary>The outcome of one evaluation: the matched rule's target, id, reason and the
/// debounce the engine must apply before committing.</summary>
public sealed record PhaseDecision(FlightPhase Target, string RuleId, string Reason, TimeSpan Debounce);
