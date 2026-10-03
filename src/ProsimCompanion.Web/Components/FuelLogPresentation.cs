using System.Globalization;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Flight;

namespace ProsimCompanion.Web.Components;

/// <summary>The Plan vs Actual header figures of the Fuel Log page (issue #154).</summary>
/// <param name="PlanHereKg">The OFP's fuel at the aircraft's point on the navlog.</param>
/// <param name="EstimatedLandingKg">Planned landing fuel moved by the present delta — the
/// fuel check's own rule: the plan's burn from here is the plan's business.</param>
public sealed record FuelLogHeader(
    double? FuelOnBoardKg,
    double? PlanHereKg,
    double? DeltaKg,
    double? EstimatedLandingKg,
    double PlannedLandingKg,
    string? BetweenFrom,
    string? BetweenTo,
    string? Trend,
    IReadOnlyList<double> RecentDeltas);

/// <summary>One point of the burn chart in SVG user units.</summary>
public sealed record FuelChartPoint(double X, double Y, string Ident, bool Actual);

/// <summary>The burn chart laid out: planned polyline, actual polyline + dots, the planned
/// landing line, the estimate at the end, and the labels — all in a fixed viewBox so the CSS
/// scales it.</summary>
public sealed record FuelChartView(
    double Width,
    double Height,
    string PlannedPoints,
    string ActualPoints,
    IReadOnlyList<FuelChartPoint> ActualDots,
    IReadOnlyList<(double Y, string Label)> GridLines,
    IReadOnlyList<(double X, string Label)> FixLabels,
    double? PlannedLandingY,
    (double X, double Y, string Label)? Estimate,
    string ProjectionPoints);

/// <summary>Pure view-model rules for the Fuel Log page: the header figures and the chart
/// geometry. No stores, no clock — the page feeds snapshots in; the tests feed records.</summary>
public static class FuelLogPresentation
{
    public static FuelLogHeader Header(FuelLogSnapshot log, IReadOnlyList<OfpFix> navlog, GeoPoint? position, double? fuelOnBoardKg)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(navlog);

        double? planHere = null;
        string? from = null, to = null;
        if (position is { } at && navlog.Count >= 2 && NavlogProgress.LastFixPassed(navlog, at) is { } passed)
        {
            planHere = NavlogProgress.PlannedFuelAt(navlog, passed.Index, passed.Fraction);
            from = navlog[passed.Index].Ident;
            to = passed.Index + 1 < navlog.Count ? navlog[passed.Index + 1].Ident : null;
        }

        var fob = fuelOnBoardKg is > 0 ? Math.Round(fuelOnBoardKg.Value) : (double?)null;
        var delta = fob is { } f && planHere is { } p ? Math.Round(f - p) : (double?)null;
        var landing = log.PlannedLandingKg > 0 && delta is { } d ? Math.Round(log.PlannedLandingKg + d) : (double?)null;
        var recent = log.Rows.Where(r => r.DeltaKg is not null).Select(r => r.DeltaKg!.Value).TakeLast(3).ToList();
        return new FuelLogHeader(fob, planHere, delta, landing, log.PlannedLandingKg, from, to, FuelLogCore.Trend(log), recent);
    }

    /// <summary>"−90 · −160 · −130 at the last three fixes" for the delta tile's sub-line.</summary>
    public static string RecentDeltasLine(IReadOnlyList<double> recent)
    {
        ArgumentNullException.ThrowIfNull(recent);
        if (recent.Count == 0)
        {
            return "no fix passed yet";
        }

        var parts = recent.Select(d => Signed(d));
        return string.Join(" · ", parts) + (recent.Count == 1 ? " at the last fix" : $" at the last {Words(recent.Count)} fixes");
    }

    public static string Signed(double kg) => (kg > 0 ? "+" : kg < 0 ? "−" : "") + Math.Abs(kg).ToString("N0", CultureInfo.InvariantCulture);

    private static string Words(int n) => n switch { 2 => "two", 3 => "three", _ => n.ToString(CultureInfo.InvariantCulture) };

    /// <summary>Lays the chart out. X is the fix index (even spacing reads better than
    /// distance on a navlog with SID/STAR fixes bunched at the ends); Y spans the plan's
    /// range with headroom. Null without at least two planned figures.</summary>
    public static FuelChartView? Chart(FuelLogSnapshot log, double? fuelOnBoardKg, double? estimatedLandingKg, double width = 660, double height = 236)
    {
        ArgumentNullException.ThrowIfNull(log);
        var planned = log.Rows.Where(r => r.PlannedFobKg is not null).ToList();
        if (planned.Count < 2)
        {
            return null;
        }

        const double padL = 44, padR = 16, padT = 16, padB = 26;
        var count = log.Rows.Count;
        var allValues = log.Rows.SelectMany(r => new[] { r.PlannedFobKg, r.ActualFobKg }).Where(v => v is not null).Select(v => v!.Value)
            .Concat(new[] { log.PlannedLandingKg, estimatedLandingKg ?? log.PlannedLandingKg, fuelOnBoardKg ?? 0 }.Where(v => v > 0))
            .ToList();
        var top = Math.Ceiling(allValues.Max() / 1000.0) * 1000 + 500;
        var bottom = Math.Max(0, Math.Floor(allValues.Min() / 1000.0) * 1000 - 500);
        if (top - bottom < 1000)
        {
            top = bottom + 1000;
        }

        double X(int i) => padL + (count <= 1 ? 0 : (i / (double)(count - 1)) * (width - padL - padR));
        double Y(double kg) => padT + ((1 - ((kg - bottom) / (top - bottom))) * (height - padT - padB));

        string Pts(IEnumerable<(int I, double Kg)> pts) => string.Join(" ", pts.Select(p => $"{X(p.I).ToString("0.#", CultureInfo.InvariantCulture)},{Y(p.Kg).ToString("0.#", CultureInfo.InvariantCulture)}"));

        var plannedPoints = Pts(log.Rows.Where(r => r.PlannedFobKg is not null).Select(r => (r.Index, r.PlannedFobKg!.Value)));
        var actual = log.Rows.Where(r => r.ActualFobKg is not null).Select(r => (r.Index, r.ActualFobKg!.Value)).ToList();
        var actualPoints = Pts(actual);
        var dots = actual.Select(a => new FuelChartPoint(X(a.Index), Y(a.Item2), log.Rows[a.Index].Ident, true)).ToList();

        var step = top - bottom > 6000 ? 2000 : 1000;
        var grid = new List<(double, string)>();
        for (var kg = Math.Ceiling(bottom / step) * step; kg <= top; kg += step)
        {
            grid.Add((Y(kg), (kg / 1000).ToString("0.#", CultureInfo.InvariantCulture) + "t"));
        }

        var labels = new List<(double, string)>();
        var every = count <= 8 ? 1 : count <= 16 ? 2 : (int)Math.Ceiling(count / 8.0);
        for (var i = 0; i < count; i++)
        {
            if (i % every == 0 || i == count - 1)
            {
                labels.Add((X(i), log.Rows[i].Ident));
            }
        }

        double? landingY = log.PlannedLandingKg > 0 ? Y(log.PlannedLandingKg) : null;
        (double, double, string)? estimate = null;
        var projection = "";
        if (estimatedLandingKg is { } est && actual.Count > 0)
        {
            estimate = (X(count - 1), Y(est), "EST " + est.ToString("N0", CultureInfo.InvariantCulture));
            var last = actual[^1];
            projection = Pts([last, (count - 1, est)]);
        }

        return new FuelChartView(width, height, plannedPoints, actualPoints, dots, grid, labels, landingY, estimate, projection);
    }
}
