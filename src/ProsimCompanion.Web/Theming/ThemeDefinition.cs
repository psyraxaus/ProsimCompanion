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
/// The raw theme colour tokens. Property defaults are the "Default" navy EFB palette so a
/// partial theme file still renders coherently. Status colours (success/warning/danger) and
/// instrument palettes are deliberately not themeable — they convey state, not brand.
/// </summary>
public sealed class ThemeColors
{
    public string PrimaryColor { get; set; } = "#2196F3";

    public string SecondaryColor { get; set; } = "#1976D2";

    public string AccentColor { get; set; } = "#FF6A33";

    public string HeaderBackground { get; set; } = "#1F2540";

    public string TabBarBackground { get; set; } = "#1F2540";

    public string ContentBackground { get; set; } = "#1A2035";

    public string SectionBackground { get; set; } = "#232A47";

    public string HeaderText { get; set; } = "#FFFFFF";

    public string ContentText { get; set; } = "#FFFFFF";

    public string CategoryText { get; set; } = "#5AABFF";
}
