using System.Globalization;
using System.Text;
using ProsimCompanion.Core.Logbook;

namespace ProsimCompanion.Core.Day;

/// <summary>
/// Composes the day's deterministic texts (turnaround line, end-of-day summary) and the
/// idempotent logbook day record. Every figure comes from <see cref="DayMath"/> — the ONE
/// duty/block/delay formula set — so what is spoken, shown and recorded can never disagree.
/// Deterministic template only, per this repo's debrief philosophy (LLM styling deferred).
/// </summary>
public interface IDaySummaryComposer
{
    /// <summary>The spoken end-of-day summary: sectors, block/duty as "h hour(s) m minute(s)",
    /// schedule delta, stabilized-approach record, abnormals, drills, fuel used, the tech-log
    /// entries raised today by name (ADR-0019 §8, issue #222 — three at most, then "and n
    /// more"); <paramref name="defectsRaised"/> null or empty falls back to the raised/cleared
    /// counts the legs carry.</summary>
    string Compose(DayState day, DateTimeOffset nowUtc, IReadOnlyList<string>? defectsRaised = null);

    /// <summary>The logbook day record (folded idempotently by DayId).</summary>
    LogbookDay BuildRecord(DayState day, DateTimeOffset nowUtc);
}

/// <inheritdoc cref="IDaySummaryComposer"/>
public sealed class DaySummaryComposer : IDaySummaryComposer
{
    /// <summary>The industry on-time threshold, minutes past the schedule (issue #185).</summary>
    public const int OnTimeToleranceMinutes = 15;

    /// <summary>Tech-log entries named in the spoken debrief before "and n more".</summary>
    public const int MaxNamedDefects = 3;

    public string Compose(DayState day, DateTimeOffset nowUtc, IReadOnlyList<string>? defectsRaised = null)
    {
        ArgumentNullException.ThrowIfNull(day);

        var legs = CompletedLegs(day);
        var sectors = legs.Count;
        var block = DayMath.BlockMinutes(day);
        var duty = DayMath.DutyMinutes(day, nowUtc);
        var delay = DayMath.DelayMinutes(day);
        var stabilized = legs.Count(l => l.Stabilized == true);
        var judged = legs.Count(l => l.Stabilized is not null);
        var abnormals = legs.Sum(l => l.Abnormals);
        var drills = legs.Sum(l => l.MemoryDrills);
        var raised = legs.Sum(l => l.DefectsRaised);
        var rectified = legs.Sum(l => l.DefectsRectified);

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"That's the duty day complete. {sectors} sector{DayMath.Plural(sectors)} flown. ");
        sb.Append(CultureInfo.InvariantCulture, $"Total block {DayMath.FormatHoursMinutes(block)}, duty {DayMath.FormatHoursMinutes(duty)}.");
        if (delay is { } dm && dm != 0)
        {
            sb.Append(dm > 0
                ? $" We finished {dm} minute{DayMath.Plural(dm)} behind schedule."
                : $" We finished {-dm} minute{DayMath.Plural(-dm)} ahead of schedule.");
        }

        // Punctuality (issue #185): the sectors that had a schedule, off blocks within the
        // industry 15 minutes.
        var scheduled = legs.Count(l => DayMath.ParseUtc(l.ScheduledOffUtc) is not null && DayMath.ParseUtc(l.ActualOffUtc) is not null);
        if (scheduled > 0)
        {
            var onTime = legs.Count(l => DayMath.ParseUtc(l.ScheduledOffUtc) is { } std && DayMath.ParseUtc(l.ActualOffUtc) is { } actual
                && (actual - std).TotalMinutes <= OnTimeToleranceMinutes);
            sb.Append(CultureInfo.InvariantCulture, $" {onTime} of {scheduled} sector{DayMath.Plural(scheduled)} off blocks on time.");
        }

        if (judged > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $" {stabilized} of {judged} approach{(judged == 1 ? "" : "es")} stabilized.");
        }

        if (abnormals > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $" {abnormals} abnormal{DayMath.Plural(abnormals)} handled.");
        }

        if (drills > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $" {drills} memory drill{DayMath.Plural(drills)} called.");
        }

        // Fuel (ADR-0019 §8, issue #222): the sum of the legs' takeoff-minus-on-blocks burns;
        // a leg without fuel stamps contributes nothing and says so when it is the only data.
        var fuelLegs = legs.Where(l => l.FuelUsedKg is > 0).ToList();
        if (fuelLegs.Count > 0)
        {
            var fuel = fuelLegs.Sum(l => l.FuelUsedKg!.Value);
            sb.Append(CultureInfo.InvariantCulture, $" Fuel used {FormatFuel(fuel)}");
            sb.Append(fuelLegs.Count < legs.Count
                ? $" over {fuelLegs.Count} of the {legs.Count} sectors."
                : ".");
        }

        // The tech-log entries by name (issue #222): "Tech log: APU bleed fault, Cabin reading
        // light 12C and Fuel quantity indication, and 2 more." Falls back to the counts when
        // the caller has no titles (an older recording, a tech log that is switched off).
        var titles = (defectsRaised ?? []).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToList();
        if (titles.Count > 0)
        {
            var named = titles.Take(MaxNamedDefects).ToList();
            var more = titles.Count - named.Count;
            sb.Append(CultureInfo.InvariantCulture, $" Tech log entr{(titles.Count == 1 ? "y" : "ies")} raised: {JoinSpoken(named)}");
            if (more > 0)
            {
                sb.Append(CultureInfo.InvariantCulture, $", and {more} more");
            }

            sb.Append(rectified > 0 ? $"; {rectified} cleared." : ".");
        }
        else if (raised > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $" {raised} defect{DayMath.Plural(raised)} raised{(rectified > 0 ? $", {rectified} cleared" : "")}.");
        }

        return sb.ToString();
    }

    /// <summary>"4.2 tonnes" from 1,000 kg up (one decimal), "850 kilograms" below — the
    /// figure a crew would say, never a raw kilogram count in the thousands.</summary>
    public static string FormatFuel(double kg)
    {
        if (kg >= 1000)
        {
            var tonnes = Math.Round(kg / 1000, 1, MidpointRounding.AwayFromZero);
            return $"{tonnes.ToString("0.0", CultureInfo.InvariantCulture)} tonnes";
        }

        var rounded = (int)Math.Round(kg / 10, MidpointRounding.AwayFromZero) * 10;
        return $"{rounded.ToString(CultureInfo.InvariantCulture)} kilogram{DayMath.Plural(rounded)}";
    }

    private static string JoinSpoken(IReadOnlyList<string> items)
        => items.Count switch
        {
            0 => "",
            1 => items[0],
            2 => $"{items[0]} and {items[1]}",
            _ => string.Join(", ", items.Take(items.Count - 1)) + $" and {items[^1]}",
        };

    public LogbookDay BuildRecord(DayState day, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(day);
        var legs = CompletedLegs(day);
        return new LogbookDay
        {
            DayId = day.DayId,
            Date = (DayMath.ParseUtc(day.StartedUtc) ?? nowUtc)
                .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Legs = legs.Count,
            Route = RouteList(legs),
            BlockMinutes = DayMath.BlockMinutes(day),
            DutyMinutes = DayMath.DutyMinutes(day, nowUtc),
            DelayMinutes = DayMath.DelayMinutes(day),
            StabilizedApproaches = legs.Count(l => l.Stabilized == true),
            JudgedApproaches = legs.Count(l => l.Stabilized is not null),
            Abnormals = legs.Sum(l => l.Abnormals),
            MemoryDrills = legs.Sum(l => l.MemoryDrills),
            DefectsRaised = legs.Sum(l => l.DefectsRaised),
            DefectsRectified = legs.Sum(l => l.DefectsRectified),
        };
    }

    /// <summary>The FO's one FDP line at the turnaround (issue #189), only when the next sector
    /// would run past the table's maximum: "Flight duty check: the next sector would run to
    /// 18:40 zulu — past the 11 hours 30 maximum for a 07:10 report with 4 sectors. Acclimatisation
    /// and extensions are not counted."</summary>
    public static string FdpWarningLine(DateTimeOffset projectedEndUtc, int maxFdpMinutes, DateTimeOffset reportUtc, int sectors)
        => $"Flight duty check: the next sector would run to {projectedEndUtc:HH\\:mm} zulu — past the "
            + $"{DayMath.FormatHoursMinutes(maxFdpMinutes)} maximum for a {reportUtc:HH\\:mm} report with {sectors} sector{DayMath.Plural(sectors)}. "
            + "Acclimatisation and extensions are not counted.";

    /// <summary>The FO's spoken turnaround line at on-blocks: "That's leg {n} — {block}
    /// minute(s) block", the schedule delta when a scheduled on-blocks exists (null delay =
    /// nothing to compare), and the open tech-log count for the next sector.</summary>
    public static string TurnaroundLine(int legIndex, int blockMinutes, int? delayMinutes, int openDefects)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"That's leg {legIndex} — {blockMinutes} minute{DayMath.Plural(blockMinutes)} block");
        if (delayMinutes is { } dm && dm != 0)
        {
            sb.Append(dm > 0
                ? $", {dm} minute{DayMath.Plural(dm)} behind schedule"
                : $", {-dm} minute{DayMath.Plural(-dm)} ahead of schedule");
        }
        else if (delayMinutes == 0)
        {
            sb.Append(", on schedule");
        }

        sb.Append('.');
        if (openDefects > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $" {openDefects} item{DayMath.Plural(openDefects)} still in the tech log for the next sector.");
        }

        return sb.ToString();
    }

    private static List<DayLeg> CompletedLegs(DayState day)
        => day.Legs.Where(l => !string.IsNullOrEmpty(l.ActualOnUtc)).OrderBy(l => l.Index).ToList();

    private static List<string> RouteList(List<DayLeg> legs)
    {
        var route = new List<string>();
        foreach (var leg in legs)
        {
            if (route.Count == 0 && !string.IsNullOrEmpty(leg.From))
            {
                route.Add(leg.From!);
            }

            if (!string.IsNullOrEmpty(leg.To))
            {
                route.Add(leg.To!);
            }
        }

        return route;
    }
}
