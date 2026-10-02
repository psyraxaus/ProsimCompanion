using System.Globalization;
using ProsimCompanion.Core.Logbook;

namespace ProsimCompanion.Web.Components;

/// <summary>The Logbook table's sortable columns.</summary>
public enum LogbookSort
{
    Date,
    Route,
    Block,
    Flight,
    TouchdownRate,
    TouchdownSpeed,
    Approach,
}

/// <summary>One landing on the touchdown-rate trend.</summary>
/// <param name="X">Centre, SVG user units.</param>
/// <param name="Y">Centre, SVG user units.</param>
/// <param name="Label">The hover text ("2026-10-03 EGLL → LIRF · -180 fpm").</param>
public sealed record SparkPoint(double X, double Y, string Label);

/// <summary>The touchdown-rate trend, laid out in its own viewBox.</summary>
/// <param name="Path">SVG path through every point, oldest left; empty below two points.</param>
/// <param name="AverageY">Y of the average line; null below two points.</param>
public sealed record LogbookSparkline(IReadOnlyList<SparkPoint> Points, string Path, double? AverageY)
{
    public const double Width = 320;
    public const double Height = 64;

    /// <summary>Room for the end marker and its ring inside the viewBox.</summary>
    public const double Pad = 8;

    public static string Px(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);
}

/// <summary>
/// Pure presentation rules of the Logbook page (issue #146) — sorting, figure texts and the
/// sparkline geometry — kept out of the page so they are testable.
/// </summary>
public static class LogbookPresentation
{
    /// <summary>How many of the most recent measured landings the trend shows.</summary>
    public const int SparklineLandings = 30;

    /// <summary>Sorts for the table. A flight with no value in the sorted column always goes
    /// to the bottom — in BOTH directions — so "firmest landing first" never opens with a
    /// page of blanks. Ties keep newest-first order.</summary>
    public static IReadOnlyList<LogbookFlight> Sorted(IEnumerable<LogbookFlight> flights, LogbookSort sort, bool descending)
    {
        ArgumentNullException.ThrowIfNull(flights);

        // Newest first as the stable base: the store appends in fold order.
        var newestFirst = flights.Reverse().ToList();
        Func<LogbookFlight, IComparable?> key = sort switch
        {
            LogbookSort.Route => f => Route(f) is { Length: > 0 } route && route != "—" ? route : null,
            LogbookSort.Block => f => f.BlockMinutes,
            LogbookSort.Flight => f => f.FlightMinutes,
            LogbookSort.TouchdownRate => f => f.TouchdownVerticalSpeedFpm,
            LogbookSort.TouchdownSpeed => f => f.TouchdownGroundSpeedKt,
            LogbookSort.Approach => f => string.IsNullOrEmpty(f.ApproachResult) ? null : f.ApproachResult,
            // ISO dates and session ids ("session-yyyyMMdd-HHmmss") both sort as text.
            _ => f => string.IsNullOrEmpty(f.SessionId) ? null : f.SessionId,
        };

        var withValue = newestFirst.Where(f => key(f) is not null);
        var ordered = descending
            ? withValue.OrderByDescending(key, Comparer<IComparable?>.Create(Compare))
            : withValue.OrderBy(key, Comparer<IComparable?>.Create(Compare));
        return [.. ordered, .. newestFirst.Where(f => key(f) is null)];
    }

    private static int Compare(IComparable? a, IComparable? b)
        => a is string left && b is string right
            ? string.CompareOrdinal(left, right)
            : Comparer<IComparable?>.Default.Compare(a, b);

    /// <summary>The trend over the most recent measured landings, oldest left. The vertical
    /// scale is the data's own range (a firm landing sits low, a soft one high); one landing
    /// is a single centred point, none is an empty chart.</summary>
    public static LogbookSparkline Sparkline(IEnumerable<LogbookFlight> flights)
    {
        ArgumentNullException.ThrowIfNull(flights);

        var measured = flights
            .Where(f => f.Landed && f.TouchdownVerticalSpeedFpm is { } rate && double.IsFinite(rate))
            .TakeLast(SparklineLandings)
            .ToList();
        if (measured.Count == 0)
        {
            return new LogbookSparkline([], "", null);
        }

        var rates = measured.Select(f => f.TouchdownVerticalSpeedFpm!.Value).ToList();
        var min = rates.Min();
        var max = rates.Max();
        var span = max - min;
        const double left = LogbookSparkline.Pad;
        const double right = LogbookSparkline.Width - LogbookSparkline.Pad;
        const double top = LogbookSparkline.Pad;
        const double bottom = LogbookSparkline.Height - LogbookSparkline.Pad;

        double Y(double rate) => span <= 0 ? (top + bottom) / 2 : bottom - ((rate - min) / span * (bottom - top));
        double X(int index) => measured.Count == 1 ? (left + right) / 2 : left + (index * (right - left) / (measured.Count - 1));

        var points = measured
            .Select((f, i) => new SparkPoint(X(i), Y(rates[i]), $"{f.Date} {Route(f)} · {Rate(rates[i])} fpm"))
            .ToList();
        var path = points.Count < 2
            ? ""
            : string.Join(' ', points.Select((p, i) =>
                $"{(i == 0 ? 'M' : 'L')}{LogbookSparkline.Px(p.X)} {LogbookSparkline.Px(p.Y)}"));
        return new LogbookSparkline(points, path, points.Count < 2 ? null : Y(rates.Average()));
    }

    /// <summary>"EGLL → LIRF", one end as "?" when unknown, "—" when both are.</summary>
    public static string Route(LogbookFlight flight)
    {
        ArgumentNullException.ThrowIfNull(flight);
        var origin = string.IsNullOrWhiteSpace(flight.Origin) ? null : flight.Origin;
        var destination = string.IsNullOrWhiteSpace(flight.Destination) ? null : flight.Destination;
        return origin is null && destination is null ? "—" : $"{origin ?? "?"} → {destination ?? "?"}";
    }

    /// <summary>"-180" / "—": whole feet per minute, sign kept (a touchdown rate is negative).</summary>
    public static string Rate(double? verticalSpeedFpm)
        => verticalSpeedFpm is { } rate && double.IsFinite(rate)
            ? Math.Round(rate).ToString("0", CultureInfo.InvariantCulture)
            : "—";

    /// <summary>"1h 52m" / "48m" / "—".</summary>
    public static string Minutes(int? minutes)
        => minutes switch
        {
            null or < 0 => "—",
            >= 60 => $"{minutes.Value / 60}h {minutes.Value % 60:00}m",
            _ => $"{minutes.Value}m",
        };

    /// <summary>"142" / "—" for a speed in knots.</summary>
    public static string Knots(double? knots)
        => knots is { } value && double.IsFinite(value)
            ? Math.Round(value).ToString("0", CultureInfo.InvariantCulture)
            : "—";

    /// <summary>"+3.5°" / "—".</summary>
    public static string Degrees(double? degrees)
        => degrees is { } value && double.IsFinite(value)
            ? value.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + "°"
            : "—";

    /// <summary>"2026-10-03 13:42Z" / "—".</summary>
    public static string Stamp(DateTimeOffset? utc)
        => utc is { } value
            ? value.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "Z"
            : "—";

    /// <summary>"92 %" / "—" (no rate without a judged approach).</summary>
    public static string Percent(double? percent)
        => percent is { } value ? value.ToString("0", CultureInfo.InvariantCulture) + " %" : "—";

    /// <summary>Pill tone for an approach verdict.</summary>
    public static string ApproachTone(string? result) => result switch
    {
        "stable" => "tone-ok",
        "unstable" => "tone-bad",
        _ => "tone-neutral",
    };
}
