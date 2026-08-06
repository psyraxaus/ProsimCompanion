using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Flight;

namespace ProsimCompanion.Speech.Arbiter;

/// <summary>What a suppression rule wants done with a request at dequeue time.</summary>
public enum SpeechSuppressionVerdict
{
    /// <summary>No objection — speak it.</summary>
    Allow,

    /// <summary>Hold it; deferred items are re-judged periodically and readmitted to the tail
    /// of their priority queue once every rule relents.</summary>
    Defer,

    /// <summary>Veto — the item resolves <see cref="SpeechOutcome.Suppressed"/>.</summary>
    Suppress,
}

/// <summary>The flight situation suppression rules judge against.</summary>
/// <param name="Phase">Committed phase from the central engine.</param>
/// <param name="AltitudeMslFt">Baro altitude MSL, ft (the sterile ceiling is MSL, not AGL).</param>
/// <param name="IsValid">False while flight data is stale/absent — rules must fail open.</param>
public sealed record SpeechContext(FlightPhase Phase, double AltitudeMslFt, bool IsValid)
{
    /// <summary>Neutral context before the first flight-data sample — suppresses nothing.</summary>
    public static SpeechContext Unknown { get; } = new(FlightPhase.Unknown, 0, false);
}

/// <summary>
/// A pluggable gate in front of speech playback, evaluated at dequeue (not submission — the
/// situation may change while an item waits) and again while items sit deferred. Combination
/// rule across rules: any Suppress wins, else any Defer, else Allow; a rule that throws is
/// ignored for that evaluation.
/// </summary>
public interface ISpeechSuppressionRule
{
    /// <summary>Rule tag for the decision log.</summary>
    string Name { get; }

    SpeechSuppressionVerdict Evaluate(SpeechRequest request, SpeechContext context);
}

/// <summary>
/// The sterile-cockpit rule, semantics carried from Prosim2FO: sterile is airborne workload —
/// InitialClimb/Climb/Descent/Approach below the ceiling (cruise and every ground phase are
/// deliberately NOT sterile), and only with valid data (zero altitude means "no usable data",
/// not "very sterile"). High/Critical always pass; <c>cabin.*</c>-tagged reports are exempt by
/// tag rather than by inflating their priority; Low is dropped; Normal follows the configured
/// policy, defaulting to Allow because Normal carries checklists and briefings, which are
/// required during a low-altitude approach.
/// </summary>
public sealed class SterileCockpitRule : ISpeechSuppressionRule
{
    private readonly Func<SpeechOptions> _options;

    /// <param name="options">Live options accessor so settings changes apply without
    /// rebuilding the rule chain.</param>
    public SterileCockpitRule(Func<SpeechOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public string Name => "sterileCockpit";

    public SpeechSuppressionVerdict Evaluate(SpeechRequest request, SpeechContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var options = _options();
        if (!IsSterile(options, context))
        {
            return SpeechSuppressionVerdict.Allow;
        }

        if (request.Priority >= SpeechPriority.High)
        {
            return SpeechSuppressionVerdict.Allow; // Safety/flight-deck calls are never gated.
        }

        if (options.SterileExemptCabinReports
            && request.Tag?.StartsWith("cabin.", StringComparison.OrdinalIgnoreCase) == true)
        {
            return SpeechSuppressionVerdict.Allow;
        }

        if (request.Priority == SpeechPriority.Low)
        {
            return options.SterileSuppressLow
                ? SpeechSuppressionVerdict.Suppress
                : SpeechSuppressionVerdict.Allow;
        }

        return options.SterileNormalPolicy switch
        {
            SterileNormalPolicy.Defer => SpeechSuppressionVerdict.Defer,
            SterileNormalPolicy.Suppress => SpeechSuppressionVerdict.Suppress,
            _ => SpeechSuppressionVerdict.Allow,
        };
    }

    /// <summary>Whether the sterile regime is active — shared with the /speech status page.</summary>
    public static bool IsSterile(SpeechOptions options, SpeechContext context)
        => options.SterileEnabled
            && context.Phase is FlightPhase.InitialClimb or FlightPhase.Climb
                or FlightPhase.Descent or FlightPhase.Approach
            && context.IsValid
            && context.AltitudeMslFt > 0
            && context.AltitudeMslFt < options.SterileCockpitCeilingFt;
}
