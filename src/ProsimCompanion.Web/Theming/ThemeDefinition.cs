namespace ProsimCompanion.Web.Theming;

/// <summary>
/// An airline/user theme as stored in a theme JSON file. The schema is the Prosim2GSX theme
/// format (shared there between the WPF app and web EFB) so themes users made for the
/// predecessor can be dropped into <c>config/themes</c> unchanged. Unknown fields (e.g. the
/// predecessor's <c>flightPhaseColors</c>) are ignored on load.
/// </summary>
public sealed class ThemeDefinition
{
    public string Name { get; set; } = "";

    public string Description { get; set; } = "";

    public ThemeColors Colors { get; set; } = new();
}

/// <summary>
/// The raw theme colour tokens. Property defaults are the "KLM Royal Dutch" navy palette —
/// the default look since the 2026-09-20 Superdesign restyle (ADR-0011) — so a partial theme
/// file still renders coherently. Status colours (success/warning/danger) and instrument
/// palettes are deliberately not themeable — they convey state, not brand.
///
/// Token roles under the restyle: <see cref="PrimaryColor"/> is the "live data" accent (cyan
/// in the default theme; links, active tab, glowing figures), <see cref="AccentColor"/> is the
/// warm highlight (gold; primary buttons, selected chips, brand mark), and
/// <see cref="CategoryText"/> is the dim caps colour of card titles and labels.
/// </summary>
public sealed class ThemeColors
{
    public string PrimaryColor { get; set; } = "#00D9FF";

    public string SecondaryColor { get; set; } = "#00B8D9";

    public string AccentColor { get; set; } = "#D4A373";

    public string HeaderBackground { get; set; } = "#0F3B6F";

    public string TabBarBackground { get; set; } = "#0F3B6F";

    public string ContentBackground { get; set; } = "#1A5490";

    public string SectionBackground { get; set; } = "#0F3B6F";

    public string HeaderText { get; set; } = "#F8FAFC";

    public string ContentText { get; set; } = "#F8FAFC";

    public string CategoryText { get; set; } = "#B0B8C1";
}
