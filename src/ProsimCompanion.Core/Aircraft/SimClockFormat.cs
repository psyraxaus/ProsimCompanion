using System.Globalization;

namespace ProsimCompanion.Core.Aircraft;

/// <summary>
/// Formats raw sim-clock dataref values for the header split-flap display (issue #71).
/// <c>simulator.zuluTime</c> is declared <c>System.TimeSpan</c> and <c>simulator.time</c>
/// <c>System.DateTime</c> in the A322 catalog, but the SDK transport may deliver strings or
/// numbers depending on path — so this accepts all plausible shapes rather than trusting one.
/// Pure and forgiving: unparseable input yields null and the caller falls back to the
/// browser's self-ticking UTC clock.
/// </summary>
public static class SimClockFormat
{
    /// <summary>"HH:MMZ" (the header clock's 6-cell format) from a raw zulu-time value, or
    /// null when the value cannot be interpreted as a time of day.</summary>
    public static string? TryFormatZuluTime(object? raw)
    {
        var time = ToTimeOfDay(raw);
        return time is { } t ? $"{t.Hours:D2}:{t.Minutes:D2}Z" : null;
    }

    /// <summary>"ddMMM" upper-case (the header date flap's 5-cell format) from a raw sim date
    /// value, or null when the value carries no date.</summary>
    public static string? TryFormatDate(object? raw)
    {
        DateTime? date = raw switch
        {
            DateTime dt => dt,
            DateTimeOffset dto => dto.UtcDateTime,
            string s when DateTime.TryParse(
                s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var dt) => dt,
            _ => null,
        };

        // A default(DateTime) is an unpopulated dataref, not a real sim date.
        return date is { } d && d > DateTime.MinValue
            ? d.ToString("ddMMM", CultureInfo.InvariantCulture).ToUpperInvariant()
            : null;
    }

    private static TimeSpan? ToTimeOfDay(object? raw)
    {
        return raw switch
        {
            TimeSpan ts => Wrap(ts),
            DateTime dt => dt.TimeOfDay,
            DateTimeOffset dto => dto.UtcDateTime.TimeOfDay,
            // Numeric = seconds since midnight (SimConnect's zulu-time convention).
            double seconds => WrapSeconds(seconds),
            float seconds => WrapSeconds(seconds),
            int seconds => WrapSeconds(seconds),
            long seconds => WrapSeconds(seconds),
            string s when TimeSpan.TryParse(s, CultureInfo.InvariantCulture, out var ts) => Wrap(ts),
            string s when DateTime.TryParse(
                s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var dt) => dt.TimeOfDay,
            _ => null,
        };
    }

    private static TimeSpan? WrapSeconds(double seconds)
        => double.IsFinite(seconds) ? Wrap(TimeSpan.FromSeconds(seconds)) : null;

    /// <summary>Wraps any span into [0, 24 h) — a zulu TimeSpan may legitimately exceed a day
    /// or run negative depending on the source's epoch handling.</summary>
    private static TimeSpan Wrap(TimeSpan value)
    {
        var day = TimeSpan.TicksPerDay;
        var ticks = ((value.Ticks % day) + day) % day;
        return TimeSpan.FromTicks(ticks);
    }
}
