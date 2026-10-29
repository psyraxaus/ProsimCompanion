namespace ProsimCompanion.Core.Aircraft;

/// <summary>
/// The simulated LOCAL time of day at the aircraft (owner report 2026-10-25: the FO opened a
/// 16:35 briefing with "good morning" — the model had no clock at all, see
/// <c>BriefingComposer.SystemPrompt</c>). MSFS's <c>LOCAL TIME</c> SimVar is seconds since
/// local midnight at the aircraft's position; ProSim's <c>simulator.time</c> is not used because
/// its time-of-day component was never verified as local vs zulu. Null while MSFS is absent or
/// the value has gone stale — callers then greet without a time of day.
/// </summary>
public interface ISimLocalTime
{
    /// <summary>Local time of day at the aircraft, or null when the sim is not live.</summary>
    TimeSpan? LocalTimeOfDay { get; }
}

/// <summary>Live <see cref="ISimLocalTime"/> over the SimConnect read model (one subscription,
/// never a poll). Degrades: with no <see cref="ISimVars"/> registered (Sim pillar off) or no
/// push yet, the clock is null.</summary>
public sealed class SimLocalTime : ISimLocalTime, IDisposable
{
    /// <summary>The SimVar pushes every second; much older than this means the connection died.</summary>
    private static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(30);

    private readonly IDataRefSubscription<double>? _localTime;

    public SimLocalTime(ISimVars? simVars = null)
    {
        _localTime = simVars?.Subscribe(ProsimDataRefNames.SimVars.LocalTimeSeconds);
    }

    /// <inheritdoc />
    public TimeSpan? LocalTimeOfDay
    {
        get
        {
            var sub = _localTime;
            if (sub is null || sub.RawValue is null || sub.IsStale
                || sub.LastUpdatedUtc is not { } updated
                || DateTimeOffset.UtcNow - updated > FreshFor)
            {
                return null;
            }

            var seconds = sub.Value;
            if (double.IsNaN(seconds) || seconds < 0 || seconds >= 86_400)
            {
                return null;
            }

            return TimeSpan.FromSeconds(Math.Floor(seconds));
        }
    }

    public void Dispose() => _localTime?.Dispose();
}

/// <summary>The word a crew greets by: morning 04:00–11:59, afternoon 12:00–17:59, evening
/// 18:00–03:59 (an overnight crew says "good evening" until the small hours). Morning starts
/// at 04:00, not 05:00: the owner's 2026-10-29 EGLL departure briefed at a 04:50 sim clock,
/// and a crew reporting for a dawn departure says "good morning". Pure.</summary>
public static class TimeOfDayGreeting
{
    public static string Word(TimeSpan localTimeOfDay)
    {
        var hour = ((int)localTimeOfDay.TotalHours % 24 + 24) % 24;
        return hour switch
        {
            >= 4 and < 12 => "morning",
            >= 12 and < 18 => "afternoon",
            _ => "evening",
        };
    }

    /// <summary>"Good afternoon" for a known clock, null otherwise.</summary>
    public static string? Greeting(TimeSpan? localTimeOfDay)
        => localTimeOfDay is { } t ? "Good " + Word(t) : null;
}
