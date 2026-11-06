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

    /// <summary>The sim's local calendar day at the aircraft (issue #194), or null when the
    /// sim is not live or the date SimVars have not pushed. Default null so a fake that only
    /// knows the clock still compiles.</summary>
    DateOnly? LocalDate => null;

    /// <summary>The sim's zulu time of day (issue #195: the sun's place for the FO's remark
    /// about the view), or null when the sim is not live. Default null for fakes.</summary>
    TimeSpan? ZuluTimeOfDay => null;
}

/// <summary>Live <see cref="ISimLocalTime"/> over the SimConnect read model (one subscription,
/// never a poll). Degrades: with no <see cref="ISimVars"/> registered (Sim pillar off) or no
/// push yet, the clock is null.</summary>
public sealed class SimLocalTime : ISimLocalTime, IDisposable
{
    /// <summary>The SimVar pushes every second; much older than this means the connection died.</summary>
    private static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(30);

    private readonly IDataRefSubscription<double>? _localTime;
    private readonly IDataRefSubscription<double>? _localYear;
    private readonly IDataRefSubscription<double>? _localDayOfYear;

    public SimLocalTime(ISimVars? simVars = null)
    {
        _localTime = simVars?.Subscribe(ProsimDataRefNames.SimVars.LocalTimeSeconds);
        _localYear = simVars?.Subscribe(ProsimDataRefNames.SimVars.LocalYear);
        _localDayOfYear = simVars?.Subscribe(ProsimDataRefNames.SimVars.LocalDayOfYear);
        _zuluTime = simVars?.Subscribe(ProsimDataRefNames.SimVars.ZuluTimeSeconds);
    }

    private readonly IDataRefSubscription<double>? _zuluTime;

    /// <inheritdoc />
    public TimeSpan? ZuluTimeOfDay
    {
        get
        {
            // Same freshness rule as the local clock: nothing without a live sim.
            if (LocalTimeOfDay is null || _zuluTime is null || _zuluTime.RawValue is null)
            {
                return null;
            }

            var seconds = _zuluTime.Value;
            if (double.IsNaN(seconds) || seconds < 0 || seconds >= 86_400)
            {
                return null;
            }

            return TimeSpan.FromSeconds(Math.Floor(seconds));
        }
    }

    /// <inheritdoc />
    public DateOnly? LocalDate
    {
        get
        {
            // The clock must be live for the date to mean anything (same freshness rule).
            if (LocalTimeOfDay is null || _localYear is null || _localDayOfYear is null
                || _localYear.RawValue is null || _localDayOfYear.RawValue is null)
            {
                return null;
            }

            var year = (int)Math.Round(_localYear.Value);
            var day = (int)Math.Round(_localDayOfYear.Value);
            // Day 366 of a non-leap year is checked here: AddDays would roll it into January
            // without a throw, so the catch that used to stand here never fired (review 2026-11-05).
            if (year < 1900 || year > 9999 || day < 1 || day > (DateTime.IsLeapYear(year) ? 366 : 365))
            {
                return null;
            }

            return new DateOnly(year, 1, 1).AddDays(day - 1);
        }
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

    public void Dispose()
    {
        _localTime?.Dispose();
        _localYear?.Dispose();
        _localDayOfYear?.Dispose();
        _zuluTime?.Dispose();
    }
}

/// <summary>The word a crew greets by: morning 04:00–11:59, afternoon 12:00–17:59, evening
/// 18:00–03:59 (an overnight crew says "good evening" until the small hours). Morning starts
/// at 04:00, not 05:00: the owner's 2026-10-29 EGLL departure briefed at a 04:50 sim clock,
/// and a crew reporting for a dawn departure says "good morning". Pure.</summary>
public static class TimeOfDayGreeting
{
    public const string Morning = "morning";
    public const string Afternoon = "afternoon";
    public const string Evening = "evening";

    public static string Word(TimeSpan localTimeOfDay)
    {
        var hour = ((int)localTimeOfDay.TotalHours % 24 + 24) % 24;
        return hour switch
        {
            >= 4 and < 12 => Morning,
            >= 12 and < 18 => Afternoon,
            _ => Evening,
        };
    }

    /// <summary>True in the morning band only (04:00–11:59) — the one yes/no a caller may
    /// need (the small-talk bank splits "local morning" from "local afternoon or evening").
    /// Issue #229: small talk tested "before noon" on its own and called 00:00–03:59 morning
    /// while the briefing of the same hour said evening; every band test lives here now.</summary>
    public static bool IsMorning(TimeSpan localTimeOfDay)
        => string.Equals(Word(localTimeOfDay), Morning, StringComparison.Ordinal);

    /// <summary>"Good afternoon" for a known clock, null otherwise.</summary>
    public static string? Greeting(TimeSpan? localTimeOfDay)
        => localTimeOfDay is { } t ? Phrase(Word(t)) : null;

    /// <summary>"Good morning" from a word this class produced earlier — the briefing and the
    /// greeting guard compose from facts that carry the word, not the clock, so the spoken
    /// form has one home too.</summary>
    public static string Phrase(string word)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(word);
        return "Good " + word;
    }
}
