using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Core.Weather;

namespace ProsimCompanion.Speech.Monitoring;

/// <summary>
/// The once-per-approach wind-limit advisory (issue #182), pure: on the Approach phase, when
/// the destination METAR puts the landing runway beyond a limit (gust included, the selected
/// runway condition's crosswind limit), one line — then silence until the next approach (a
/// go-around re-arms it when the phase leaves Approach). A wind that comes back within limits
/// says nothing: the pages and "wind check" carry the live state.
/// </summary>
public sealed class WindLimitAdvisoryCore
{
    private bool _saidThisApproach;

    /// <summary>One observation. Returns the line to speak, or null.</summary>
    public WindLimitVerdict? Observe(FlightPhase phase, WxFacts facts, double? runwayHeadingDeg, RunwayCondition? condition, AircraftLimitsOptions limits)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(limits);
        if (phase != FlightPhase.Approach)
        {
            _saidThisApproach = false;
            return null;
        }

        if (_saidThisApproach || runwayHeadingDeg is not { } heading)
        {
            return null;
        }

        var wind = WindMath.Resolve(facts.WindDirDeg, facts.WindSpeedKt, facts.WindGustKt, heading);
        if (wind is null)
        {
            return null;
        }

        var verdict = WindLimitCheck.Evaluate(wind, limits, WindPhase.Landing, condition);
        if (!verdict.Exceeded)
        {
            return null;
        }

        _saidThisApproach = true;
        return verdict;
    }

    public void Reset() => _saidThisApproach = false;
}
