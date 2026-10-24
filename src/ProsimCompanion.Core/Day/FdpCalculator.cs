namespace ProsimCompanion.Core.Day;

/// <summary>The FDP picture for the Day page banner and the turnaround line (issue #189).</summary>
/// <param name="ReportUtc">Where the FDP started.</param>
/// <param name="ReportSource">"rotation" (the rotation file's report time) or "firstSession" (day start minus the report lead).</param>
/// <param name="SectorsFlown">Legs with an on-blocks.</param>
/// <param name="SectorsWithNext">Sectors the next leg would make it.</param>
/// <param name="MaxFdpMinutes">The table's maximum for the report band and <see cref="SectorsWithNext"/>.</param>
/// <param name="FdpSoFarMinutes">Report → now (open day) or → last on-blocks.</param>
/// <param name="ProjectedEndUtc">When the next leg would end — its scheduled on-blocks, else now + the planned block.</param>
/// <param name="ProjectedFdpMinutes">Report → projected end.</param>
/// <param name="Exceeds">The projection is past the maximum.</param>
public sealed record FdpStatus(
    DateTimeOffset ReportUtc,
    string ReportSource,
    int SectorsFlown,
    int SectorsWithNext,
    int MaxFdpMinutes,
    int FdpSoFarMinutes,
    DateTimeOffset? ProjectedEndUtc,
    int? ProjectedFdpMinutes,
    bool Exceeds)
{
    /// <summary>Minutes of margin left on the projection (negative = over); null without a projection.</summary>
    public int? MarginMinutes => ProjectedFdpMinutes is { } p ? MaxFdpMinutes - p : null;
}

/// <summary>What the engine needs to compute the FDP status.</summary>
public sealed record FdpConfiguration(FtlTable Table, int ReportBeforeStdMinutes, bool Enabled);

/// <summary>
/// Pure FDP maths (issue #189). The report time is the rotation file's when it has one, else the
/// day's start minus the report lead. The next leg is the first leg without an on-blocks; its end
/// is its scheduled on-blocks when the plan has one, else now plus the planned block — or, with
/// neither, the status carries no projection and never warns. Clock passed in.
/// </summary>
public static class FdpCalculator
{
    public static FdpStatus? Evaluate(DayState day, FdpConfiguration configuration, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(day);
        ArgumentNullException.ThrowIfNull(configuration);
        if (!configuration.Enabled)
        {
            return null;
        }

        DateTimeOffset report;
        string source;
        if (DayMath.ParseUtc(day.ReportTimeUtc) is { } rotationReport)
        {
            report = rotationReport;
            source = "rotation";
        }
        else if (DayMath.ParseUtc(day.StartedUtc) is { } started)
        {
            report = started.AddMinutes(-Math.Max(0, configuration.ReportBeforeStdMinutes));
            source = "firstSession";
        }
        else
        {
            return null;
        }

        var flown = day.Legs.Count(l => !string.IsNullOrEmpty(l.ActualOnUtc));
        var next = day.Legs.OrderBy(l => l.Index).FirstOrDefault(l => string.IsNullOrEmpty(l.ActualOnUtc));
        var sectorsWithNext = flown + (next is null ? 0 : 1);
        var max = configuration.Table.MaxFdp(report, Math.Max(1, sectorsWithNext));
        if (max is not { } maxFdp)
        {
            return null;
        }

        var soFarEnd = day.IsOpen
            ? nowUtc
            : day.Legs.Select(l => DayMath.ParseUtc(l.ActualOnUtc)).Where(t => t is not null).Select(t => t!.Value).DefaultIfEmpty(report).Max();
        var soFar = (int)Math.Max(0, (soFarEnd - report).TotalMinutes);

        DateTimeOffset? projectedEnd = null;
        if (next is not null)
        {
            if (DayMath.ParseUtc(next.ScheduledOnUtc) is { } scheduledOn)
            {
                projectedEnd = scheduledOn > nowUtc ? scheduledOn : nowUtc;
            }
            else if (next.BlockMinutes is { } block && block > 0)
            {
                projectedEnd = nowUtc.AddMinutes(block);
            }

            // A scheduled off-blocks alone says nothing about the end: no projection, no warning.
        }

        int? projected = projectedEnd is { } end ? (int)Math.Max(0, (end - report).TotalMinutes) : null;
        return new FdpStatus(
            report, source, flown, sectorsWithNext, (int)maxFdp.TotalMinutes, soFar, projectedEnd, projected,
            projected is { } p && p > (int)maxFdp.TotalMinutes);
    }
}
