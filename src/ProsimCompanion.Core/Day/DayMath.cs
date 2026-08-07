using System.Globalization;

namespace ProsimCompanion.Core.Day;

/// <summary>
/// The single home of the day's derived figures. Prosim2FO computed duty minutes twice — once
/// in the dashboard view and once in the summary composer, with subtly different "no legs"
/// fallbacks — so the two could disagree on screen vs. in speech. Here there is ONE formula:
/// duty = (end − duty start), where end = now while the day is open, else the last actual
/// on-blocks plus the post-flight allowance.
/// </summary>
public static class DayMath
{
    /// <summary>Round-trip ISO-8601 parse; null for anything else.</summary>
    public static DateTimeOffset? ParseUtc(string? value)
        => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;

    /// <summary>Total block minutes flown so far (sum of the filled per-leg figures).</summary>
    public static int BlockMinutes(DayState day)
    {
        ArgumentNullException.ThrowIfNull(day);
        return day.Legs.Sum(l => l.BlockMinutes ?? 0);
    }

    /// <summary>The one duty formula (see class remarks). 0 when the duty start is missing
    /// or unparseable; never negative.</summary>
    public static int DutyMinutes(DayState day, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(day);
        if (ParseUtc(day.DutyStartUtc) is not { } start)
        {
            return 0;
        }

        DateTimeOffset end;
        if (day.IsOpen)
        {
            end = nowUtc;
        }
        else
        {
            var lastOn = day.Legs
                .Select(l => ParseUtc(l.ActualOnUtc))
                .Where(t => t is not null)
                .Select(t => t!.Value)
                .DefaultIfEmpty(start)
                .Max();
            end = lastOn.AddMinutes(day.PostFlightAllowanceMin);
        }

        return (int)Math.Max(0, (end - start).TotalMinutes);
    }

    /// <summary>Actual vs. scheduled on-blocks of the most recent completed leg that has both
    /// (positive = behind schedule); null when no leg has a schedule to compare against.</summary>
    public static int? DelayMinutes(DayState day)
    {
        ArgumentNullException.ThrowIfNull(day);
        var leg = day.Legs.LastOrDefault(l => l.ActualOnUtc is not null && l.ScheduledOnUtc is not null);
        if (leg is null || ParseUtc(leg.ActualOnUtc) is not { } actual || ParseUtc(leg.ScheduledOnUtc) is not { } scheduled)
        {
            return null;
        }

        return (int)Math.Round((actual - scheduled).TotalMinutes);
    }

    /// <summary>Builds the derived view for the given moment.</summary>
    public static DayView BuildView(DayState day, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(day);
        var current = day.Current;
        return new DayView(
            day.IsOpen,
            day.State.ToString(),
            day.CurrentLegIndex,
            day.Legs.Count,
            day.LegsCompleted,
            current?.From,
            current?.To,
            BlockMinutes(day),
            DutyMinutes(day, nowUtc),
            DelayMinutes(day));
    }

    /// <summary>"7 hours 40 minutes" / "2 hours" / "45 minutes" — the spoken and displayed
    /// duration style shared by the summary and the web page.</summary>
    public static string FormatHoursMinutes(int minutes)
    {
        minutes = Math.Max(0, minutes);
        var hours = minutes / 60;
        var rest = minutes % 60;
        if (hours == 0)
        {
            return $"{rest} minute{Plural(rest)}";
        }

        return rest == 0
            ? $"{hours} hour{Plural(hours)}"
            : $"{hours} hour{Plural(hours)} {rest} minute{Plural(rest)}";
    }

    /// <summary>"" for 1, "s" otherwise — the pluralization used across the day texts.</summary>
    public static string Plural(int n) => n == 1 ? "" : "s";
}
