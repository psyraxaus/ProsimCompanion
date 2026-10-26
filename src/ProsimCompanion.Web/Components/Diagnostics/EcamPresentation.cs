using System.Globalization;
using ProsimCompanion.Core.Diagnostics.Ecam;

namespace ProsimCompanion.Web.Components.Diagnostics;

/// <summary>
/// The one place a <see cref="DiagState"/> becomes a CSS class, and the small text rules the
/// ECAM pages share (uptime cells, dotted action lines, the version split). No Razor file
/// carries a colour: the six hexes live in diagnostics.css under the <c>st-*</c> classes
/// this map names, so a colour change is one CSS edit and this map is testable.
/// </summary>
public static class EcamPresentation
{
    /// <summary>The 720×640 drawing area the pages are laid out for.</summary>
    public const int StageWidth = 720;
    public const int StageHeight = 640;

    /// <summary>State → the <c>st-*</c> class that sets <c>--st</c> (border and text colour).</summary>
    public static string StateClass(DiagState state) => state switch
    {
        DiagState.Normal => "st-normal",
        DiagState.Caution => "st-caution",
        DiagState.Warning => "st-warning",
        DiagState.NotApplicable => "st-na",
        _ => "st-nodata",
    };

    /// <summary>The class for a node, or NoData's when the node is missing.</summary>
    public static string NodeClass(DiagPage page, string id) => StateClass(page.NodeOrNoData(id).State);

    /// <summary>A readout's class: green when present, amber (XX) when not.</summary>
    public static string ReadoutClass(string? text) => text is null ? "st-nodata" : "st-normal";

    /// <summary>A readout's text, or the amber XX token when nothing reported it.</summary>
    public static string ReadoutText(string? text) => text ?? EcamTokens.NoData;

    /// <summary>"0.8.0-" and "RC.10" from "0.8.0-rc.10"; the whole version and "" without a dash.</summary>
    public static (string Main, string Tag) VersionParts(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return (EcamTokens.NoData, "");
        }

        var dash = version.IndexOf('-', StringComparison.Ordinal);
        return dash < 0
            ? (version.ToUpperInvariant(), "")
            : (version[..(dash + 1)].ToUpperInvariant(), version[(dash + 1)..].ToUpperInvariant());
    }

    /// <summary>"02" and "14" for an uptime of 2 h 14 min (hours are not capped at 24).</summary>
    public static (string Hours, string Minutes) UptimeParts(TimeSpan uptime)
        => (((int)uptime.TotalHours).ToString("00", CultureInfo.InvariantCulture), uptime.Minutes.ToString("00", CultureInfo.InvariantCulture));

    /// <summary>HH:mm in the browser's local time is not known server-side; the STATUS page
    /// shows the server's local clock, which is the sim PC's, which is what the pilot has.</summary>
    public static string Clock(DateTimeOffset at) => at.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>"- WOL MAGIC PKT ........ SENT 00:42": the label, a dotted leader to a fixed
    /// column, the value. Width is in characters of the mono face.</summary>
    public static string DottedAction(DiagAction action, int width = 34)
    {
        ArgumentNullException.ThrowIfNull(action);
        var label = "- " + action.Label.ToUpperInvariant() + " ";
        var value = " " + action.Value.ToUpperInvariant();
        var dots = Math.Max(2, width - label.Length - value.Length);
        return label + new string('.', dots) + value;
    }

    /// <summary>The DIAG state class of a caution title (green notes stay green).</summary>
    public static string CautionClass(DiagCaution caution) => StateClass(caution.Severity);

    /// <summary>Upper-case with the ASCII arrow kept (the route row).</summary>
    public static string Upper(string? text) => (text ?? "").ToUpperInvariant();

    /// <summary>Gauge geometry: a 270° arc from 7 o'clock clockwise to 5 o'clock. Returns the
    /// needle tip for <paramref name="value"/> and the start angle of the red segment, all in
    /// SVG user units for a 78×66 box with centre (39, 40) and radius 30.</summary>
    public static GaugeGeometry Gauge(DiagGauge gauge)
    {
        ArgumentNullException.ThrowIfNull(gauge);
        var span = gauge.Max - gauge.Min;
        double Fraction(double v) => span <= 0 ? 0 : Math.Clamp((v - gauge.Min) / span, 0, 1);
        var warnFraction = Fraction(gauge.WarningAt);
        var valueFraction = gauge.Value is { } v ? Fraction(v) : 0;
        return new GaugeGeometry(
            ArcPath(0, 1, 30),
            warnFraction < 1 ? ArcPath(warnFraction, 1, 30) : null,
            gauge.Value is null ? null : Point(valueFraction, 24),
            gauge.Value is { } val ? val.ToString(val >= 100 ? "0" : "0.#", CultureInfo.InvariantCulture) : EcamTokens.NoData,
            StateClass(gauge.ValueState));
    }

    private const double Cx = 39;
    private const double Cy = 40;

    private static (double X, double Y) Point(double fraction, double radius)
    {
        // 135° (7 o'clock) sweeping clockwise through 270° to 45° (5 o'clock).
        var angle = (135 + 270 * fraction) * Math.PI / 180;
        return (Math.Round(Cx + radius * Math.Cos(angle), 1), Math.Round(Cy + radius * Math.Sin(angle), 1));
    }

    private static string ArcPath(double from, double to, double radius)
    {
        var (x1, y1) = Point(from, radius);
        var (x2, y2) = Point(to, radius);
        var large = (to - from) * 270 > 180 ? 1 : 0;
        return string.Create(CultureInfo.InvariantCulture, $"M{x1} {y1} A{radius} {radius} 0 {large} 1 {x2} {y2}");
    }

    /// <summary>A memo-strip value with an optional small cyan qualifier ("02 H 14" → "02", "H 14").</summary>
    public static string Or(string? text, string fallback) => string.IsNullOrWhiteSpace(text) ? fallback : text;

    /// <summary>The nav badge: the amber count, or null when nothing is up.</summary>
    public static string? BadgeText(DiagMemo memo)
    {
        ArgumentNullException.ThrowIfNull(memo);
        return memo.CautionCount > 0 ? memo.CautionCount.ToString(CultureInfo.InvariantCulture) : null;
    }
}

/// <summary>Drawing data for one <c>EcamGauge</c>.</summary>
/// <param name="ArcPath">The full green arc.</param>
/// <param name="WarningArcPath">The red segment from the warning threshold, or null when the
/// threshold sits at or beyond the maximum.</param>
/// <param name="Needle">The needle tip, or null for no value (the needle is not drawn).</param>
/// <param name="ValueText">The value as printed under the needle, or XX.</param>
/// <param name="ValueClass">The <c>st-*</c> class of the value text.</param>
public sealed record GaugeGeometry(string ArcPath, string? WarningArcPath, (double X, double Y)? Needle, string ValueText, string ValueClass);
