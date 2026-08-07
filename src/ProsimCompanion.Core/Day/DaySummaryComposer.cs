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
    /// schedule delta, stabilized-approach record, abnormals, drills, defects.</summary>
    string Compose(DayState day, DateTimeOffset nowUtc);

    /// <summary>The logbook day record (folded idempotently by DayId).</summary>
    LogbookDay BuildRecord(DayState day, DateTimeOffset nowUtc);
}

/// <inheritdoc cref="IDaySummaryComposer"/>
public sealed class DaySummaryComposer : IDaySummaryComposer
{
    public string Compose(DayState day, DateTimeOffset nowUtc)
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

        if (raised > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $" {raised} defect{DayMath.Plural(raised)} raised{(rectified > 0 ? $", {rectified} cleared" : "")}.");
        }

        return sb.ToString();
    }

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
