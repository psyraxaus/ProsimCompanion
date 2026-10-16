using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Gsx.Automation;

/// <summary>
/// The pure "does this switch movement call the next service?" decision behind the cockpit
/// smart button (<see cref="GsxOptions.SmartButtonEnabled"/>, <see cref="GsxOptions.SmartButtonDataRef"/>,
/// <see cref="GsxOptions.SmartButtonTriggerValue"/>). Kept out of <see cref="GsxAutomationService"/>
/// so the trigger policy is testable without its twenty collaborators; the service owns the
/// subscriptions and feeds every push through here.
/// </summary>
public static class SmartButtonRule
{
    /// <summary>Force-next source on the built-in INT/RAD rockers — the wording the
    /// <c>intrad-service-skip</c> probe and the predecessor's logs assert on.</summary>
    public const string DefaultSource = "INT/RAD";

    /// <summary>Force-next source when the pilot named another switch dataref.</summary>
    public const string CustomSource = "smart-button";

    /// <summary>Served by a custom trigger subscription until its first push, or when a push
    /// cannot be read as a number. No cockpit switch reports it, so it never matches a trigger
    /// value — the "InvalidCastException is impossible" guarantee of the typed path, made explicit.</summary>
    public const int NoValue = int.MinValue;

    /// <summary>True when the setting is empty: both INT/RAD rockers, today's behaviour.</summary>
    public static bool UsesDefaultRocker(string? dataRef) => string.IsNullOrWhiteSpace(dataRef);

    /// <summary>The source string recorded on the force-next decision for this configuration.</summary>
    public static string Source(string? dataRef) => UsesDefaultRocker(dataRef) ? DefaultSource : CustomSource;

    /// <summary>
    /// Whether a push of <paramref name="value"/> calls the next service. The phase/cycle guard
    /// (ground preparation only, sequence running and not complete) is deliberately not
    /// configurable: the switch is a real cockpit control in every other phase.
    /// </summary>
    public static bool ShouldFire(GsxOptions options, int value, GsxAutomationPhase phase, bool cycleStarted, bool cycleComplete)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.SmartButtonEnabled || value == NoValue || value != options.SmartButtonTriggerValue)
        {
            return false;
        }

        return phase is GsxAutomationPhase.Preparation or GsxAutomationPhase.SessionStart
            && cycleStarted
            && !cycleComplete;
    }
}
