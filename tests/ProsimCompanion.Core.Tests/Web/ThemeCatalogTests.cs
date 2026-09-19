using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.Web.Theming;
using Xunit;

namespace ProsimCompanion.Core.Tests.Web;

/// <summary>
/// The 13 built-in themes of the 2026-09-20 restyle (ADR-0011) and the compatibility rules
/// that keep an older settings.json (theme "Default") rendering.
/// </summary>
public sealed class ThemeCatalogTests
{
    private static ThemeCatalog Create()
        => new(Path.Combine(Path.GetTempPath(), "prosimcompanion-tests-no-such-themes-dir"),
            NullLogger<ThemeCatalog>.Instance);

    [Fact]
    public void ListNames_ReturnsTheThirteenCanvasThemes_DefaultFirst()
    {
        var names = Create().ListNames();

        Assert.Equal(13, names.Count);
        Assert.Equal(ThemeCatalog.DefaultThemeName, names[0]);
        Assert.Equal("KLM Royal Dutch", ThemeCatalog.DefaultThemeName);
        Assert.Contains("Light", names);
        Assert.Contains("Dark", names);
        Assert.DoesNotContain("Default", names);
        Assert.DoesNotContain("Delta", names);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Default")]
    [InlineData("default")]
    [InlineData("No Such Theme")]
    public void Get_FallsBackToKlm_ForMissingLegacyOrUnknownNames(string? requested)
    {
        var theme = Create().Get(requested);

        Assert.Equal(ThemeCatalog.DefaultThemeName, theme.Name);
        Assert.Equal("#1A5490", theme.Colors.ContentBackground);
    }

    [Fact]
    public void Get_IsCaseInsensitive()
    {
        Assert.Equal("Qatar Airways", Create().Get("qatar airways").Name);
    }

    [Fact]
    public void BuiltInDefaults_MatchTheKlmThemeFile()
    {
        // A partial user theme file inherits ThemeColors' property defaults, which must be
        // the default theme so the result stays coherent.
        var klm = Create().Get(ThemeCatalog.DefaultThemeName).Colors;
        var defaults = new ThemeColors();

        Assert.Equal(klm.ContentBackground, defaults.ContentBackground, ignoreCase: true);
        Assert.Equal(klm.SectionBackground, defaults.SectionBackground, ignoreCase: true);
        Assert.Equal(klm.PrimaryColor, defaults.PrimaryColor, ignoreCase: true);
        Assert.Equal(klm.AccentColor, defaults.AccentColor, ignoreCase: true);
    }

    [Fact]
    public void CssBuilder_EmitsGoldAndInsetTokens()
    {
        var css = ThemeCssBuilder.BuildDeclarations(new ThemeColors());

        Assert.Contains("--accent-gold: #D4A373;", css);
        Assert.Contains("--accent-gold-soft: rgba(212, 163, 115, 0.12);", css);
        Assert.Contains("--bg-inset: rgba(0,0,0,0.35);", css);
        Assert.Contains("--accent: #00D9FF;", css);
    }

    [Fact]
    public void CssBuilder_LightPanelThemes_UseDarkOverlays()
    {
        var singapore = Create().Get("Singapore Airlines").Colors;
        var css = ThemeCssBuilder.BuildDeclarations(singapore);

        Assert.Contains("--border: rgba(0,0,0,0.12);", css);
        Assert.Contains("--bg-inset: rgba(0,0,0,0.05);", css);
        Assert.Contains("--text-primary: #1A1614;", css);
    }
}
