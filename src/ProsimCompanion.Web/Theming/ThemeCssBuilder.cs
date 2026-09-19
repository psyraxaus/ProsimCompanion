using System.Globalization;
using System.Text;

namespace ProsimCompanion.Web.Theming;

/// <summary>
/// Maps theme colour tokens onto the CSS custom properties the components consume — the C#
/// port of Prosim2GSX's <c>applyTheme.ts</c>, kept rule-for-rule identical so the shipped
/// airline themes render the same here as they did there.
///
/// Beyond the raw JSON tokens, three sets of values are DERIVED (mirroring the predecessor's
/// WPF ThemeManager):
///
///   1. Input / button surface variants (<c>--bg-input</c>, <c>--bg-button</c>) shifted from
///      <c>sectionBackground</c> based on whether the theme is "dark" (avg RGB &lt; 128) —
///      lighter for dark themes, darker for light ones, so controls read as raised surfaces.
///   2. Border colours as translucent white (dark themes) or black (light themes) overlays,
///      so one border value contrasts against the card surface in both polarities.
///   3. Text variants. <c>--text-primary/secondary/muted</c> ride on cards, so they blend
///      <c>contentText</c> toward the card surface. <c>--text-on-dark(-muted)</c> are for
///      elements that ALWAYS sit on a dark bar regardless of theme (tab bar, section rail,
///      DirtyBar) — bound to <c>headerText</c>, which every shipped theme keeps white.
///      Without this split, light themes render dark-on-dark in the tab bar.
/// </summary>
public static class ThemeCssBuilder
{
    /// <summary>Builds the CSS custom-property declarations (without a selector) for a theme.</summary>
    public static string BuildDeclarations(ThemeColors colors)
    {
        ArgumentNullException.ThrowIfNull(colors);

        // Detect dark vs light surface so we know which direction to shift.
        var (r, g, b) = ParseHex(colors.SectionBackground) ?? (15, 59, 111);
        var isDark = (r + g + b) / 3.0 < 128;

        var css = new StringBuilder();
        void Set(string name, string value) =>
            css.Append(name).Append(": ").Append(value).AppendLine(";");

        Set("--bg-primary", colors.ContentBackground);
        Set("--bg-secondary", colors.TabBarBackground);
        Set("--bg-card", colors.SectionBackground);
        Set("--bg-card-hover", ShiftHex(colors.SectionBackground, isDark ? 10 : -8));

        Set("--bg-input", ShiftHex(colors.SectionBackground, isDark ? 22 : -15));
        Set("--bg-button", ShiftHex(colors.SectionBackground, isDark ? 12 : -28));

        Set("--border", isDark ? "rgba(255,255,255,0.10)" : "rgba(0,0,0,0.12)");
        Set("--border-strong", isDark ? "rgba(255,255,255,0.22)" : "rgba(0,0,0,0.22)");

        Set("--text-primary", colors.ContentText);
        Set("--text-secondary", MixHex(colors.ContentText, colors.SectionBackground, 0.3));
        Set("--text-muted", MixHex(colors.ContentText, colors.SectionBackground, 0.55));

        Set("--text-on-dark", colors.HeaderText);
        Set("--text-on-dark-muted", MixHex(colors.HeaderText, colors.TabBarBackground, 0.35));

        Set("--accent", colors.PrimaryColor);
        Set("--accent-hover", colors.SecondaryColor);
        Set("--accent-muted", Rgba(colors.PrimaryColor, 0.5));

        Set("--header-bg", colors.HeaderBackground);
        Set("--header-text", colors.HeaderText);

        Set("--category-text", colors.CategoryText);

        // Restyle 2026-09-20 (ADR-0011): the warm highlight (gold in the default theme) drives
        // primary buttons, selected chips and the brand mark; the soft variant tints chips.
        // Before the restyle AccentColor was parsed but never emitted.
        Set("--accent-gold", colors.AccentColor);
        Set("--accent-gold-soft", Rgba(colors.AccentColor, 0.12));

        // Recessed "inset" surfaces (weather text, log boxes, runway diagram) sit INSIDE a
        // card: near-black on dark themes, a light grey wash on light ones.
        Set("--bg-inset", isDark ? "rgba(0,0,0,0.35)" : "rgba(0,0,0,0.05)");

        return css.ToString();
    }

    private static string Rgba(string hex, double alpha)
    {
        if (ParseHex(hex) is not var (r, g, b))
        {
            return hex;
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"rgba({r}, {g}, {b}, {alpha})");
    }

    // Linear blend in sRGB — fades contentText toward the surface for the secondary/muted
    // variants without alpha overlays (which interact poorly with non-solid backgrounds).
    private static string MixHex(string a, string b, double t)
    {
        if (ParseHex(a) is not var (ar, ag, ab) || ParseHex(b) is not var (br, bg, bb))
        {
            return a;
        }

        return ToHex(
            (int)Math.Round(ar * (1 - t) + br * t),
            (int)Math.Round(ag * (1 - t) + bg * t),
            (int)Math.Round(ab * (1 - t) + bb * t));
    }

    // Shift each channel by delta (clamped) — the "raised surface" primitive shared with the
    // predecessor's WPF ThemeManager.Shift helper.
    private static string ShiftHex(string hex, int delta)
    {
        if (ParseHex(hex) is not var (r, g, b))
        {
            return hex;
        }

        return ToHex(
            Math.Clamp(r + delta, 0, 255),
            Math.Clamp(g + delta, 0, 255),
            Math.Clamp(b + delta, 0, 255));
    }

    private static string ToHex(int r, int g, int b) => $"#{r:x2}{g:x2}{b:x2}";

    private static (int R, int G, int B)? ParseHex(string? hex)
    {
        if (string.IsNullOrEmpty(hex) || hex[0] != '#' || hex.Length != 7)
        {
            return null;
        }

        if (int.TryParse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r)
            && int.TryParse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g)
            && int.TryParse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
        {
            return (r, g, b);
        }

        return null;
    }
}
