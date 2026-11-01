namespace ProsimCompanion.Core.Day;

/// <summary>
/// The two clocks that close a duty day nobody ended (ADR-0019 §8, issue #217), pure so the
/// EHAM turnaround can be driven through them in a test:
/// <list type="bullet">
/// <item><b>The idle backstop</b> counts from the moment the arrival let go of the aircraft —
/// deboarding complete, else on-blocks — never from the last phase edge or the last app start
/// (the old 90-minute clock reset on both, and the real EHAM turnaround of 2026-11-01 was 88
/// minutes against it). Only a new plan (the next leg) or an end of duty stops it.</item>
/// <item><b>The FO's offer</b> ("Captain, is that the end of our duty day?") comes once per
/// day after the aircraft has been cold and dark, parked, with no plan for the offer minutes —
/// the later of the idle start and the cold-and-dark edge, so a restart in the dark does not
/// skip the wait.</item>
/// </list>
/// </summary>
public static class DayIdleRules
{
    /// <summary>When the turnaround idle clock started: the recorded deboarding/on-blocks
    /// stamp, else the completed leg's on-blocks (a daystate written by an older build).
    /// Null when the day is not in a turnaround.</summary>
    public static DateTimeOffset? IdleSince(DayState day)
    {
        ArgumentNullException.ThrowIfNull(day);
        if (!day.IsOpen || day.State != DayPhase.Turnaround)
        {
            return null;
        }

        return DayMath.ParseUtc(day.IdleSinceUtc) ?? DayMath.ParseUtc(day.Current?.ActualOnUtc);
    }

    /// <summary>The backstop: an open day in a turnaround for <paramref name="autoCloseMinutes"/>
    /// (0 = never) since <see cref="IdleSince"/>.</summary>
    public static bool ShouldAutoClose(DayState day, int autoCloseMinutes, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(day);
        if (autoCloseMinutes <= 0 || IdleSince(day) is not { } since)
        {
            return false;
        }

        return (nowUtc - since).TotalMinutes >= autoCloseMinutes;
    }

    /// <summary>The offer: once per day, in a turnaround, cold and dark (the phase engine's
    /// ColdAndDark since <paramref name="coldAndDarkSinceUtc"/>; null = powered), for
    /// <paramref name="offerMinutes"/> (0 = never) since the later of the idle start and the
    /// cold-and-dark edge.</summary>
    public static bool ShouldOffer(DayState day, int offerMinutes, DateTimeOffset? coldAndDarkSinceUtc, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(day);
        if (offerMinutes <= 0 || coldAndDarkSinceUtc is not { } dark || day.EndOfDutyOfferedUtc is not null
            || IdleSince(day) is not { } since)
        {
            return false;
        }

        var from = dark > since ? dark : since;
        return (nowUtc - from).TotalMinutes >= offerMinutes;
    }
}
