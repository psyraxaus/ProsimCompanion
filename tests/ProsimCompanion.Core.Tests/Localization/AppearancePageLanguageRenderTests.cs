using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using Moq;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Localization;
using ProsimCompanion.Core.Theming;
using ProsimCompanion.Web.Localization;
using ProsimCompanion.Web.Pages;
using ProsimCompanion.Web.Theming;
using Xunit;

namespace ProsimCompanion.Core.Tests.Localization;

/// <summary>
/// Renders the real Appearance page over the shipped packs (ADR-0014): a configured language
/// draws the page in that language, an empty setting follows the browser, and the picker lists
/// "follow the browser" plus every pack by its native name. The one end-to-end proof that the
/// scoped localizer, the store and the catalog are wired the way the page expects.
/// </summary>
public sealed class AppearancePageLanguageRenderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "ProsimCompanionTests", Guid.NewGuid().ToString("N"));

    public AppearancePageLanguageRenderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup is best-effort.
        }
    }

    private async Task<string> RenderAsync(string configuredLanguage, params string[] browserLanguages)
    {
        var options = new Mock<IOptionsMonitor<WebUiOptions>>();
        options.SetupGet(o => o.CurrentValue).Returns(new WebUiOptions { Language = configuredLanguage });
        options.Setup(o => o.OnChange(It.IsAny<Action<WebUiOptions, string?>>())).Returns((IDisposable?)null);

        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(options.Object);
        services.AddSingleton(new SettingsWriter(new JsonSettingsFile(Path.Combine(_root, "settings.json"))));
        services.AddSingleton(new ThemeCatalog(Path.Combine(_root, "themes"), NullLogger<ThemeCatalog>.Instance));
        services.AddSingleton(new ThemeLogoStore(Path.Combine(_root, "logos")));
        services.AddSingleton(new AirlineLogoStore(Path.Combine(_root, "airlines")));
        services.AddSingleton(Mock.Of<IJSRuntime>());
        services.AddSingleton(new LanguagePackCatalog([ShippedPackDir()], NullLogger<LanguagePackCatalog>.Instance));
        services.AddSingleton<UiLanguageStore>();
        services.AddSingleton<ProsimCompanion.Web.Components.AdvancedSettingsStore>();
        services.AddScoped(_ => new BrowserLanguageContext { Languages = browserLanguages });
        services.AddScoped<IStringLocalizer, UiText>();

        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, NullLoggerFactory.Instance);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<AppearanceSettings>(ParameterView.Empty);
            return System.Net.WebUtility.HtmlDecode(output.ToHtmlString());
        });
    }

    [Fact]
    public async Task ConfiguredGerman_RendersTheSectionsInGerman()
    {
        var html = await RenderAsync("de", "ja");

        Assert.Contains("Sprache", html, StringComparison.Ordinal);
        Assert.Contains("Design", html, StringComparison.Ordinal);
        Assert.Contains("Bildschirm & Installation", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Screen & Install", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptySetting_FollowsTheBrowser_AndNamesWhatThatGives()
    {
        var html = await RenderAsync("", "ja-jp", "en");

        Assert.Contains("言語", html, StringComparison.Ordinal);
        // The auto option tells the pilot what "follow the browser" resolves to on this device.
        Assert.Contains("日本語", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Picker_ListsFollowTheBrowser_ThenEveryPackByNativeName()
    {
        var html = await RenderAsync("en");

        Assert.Contains("Follow the browser (here: English)", html, StringComparison.Ordinal);
        foreach (var name in new[] { "English", "Deutsch", "Español", "Français", "日本語", "한국어" })
        {
            Assert.Contains(">" + name + "<", html, StringComparison.Ordinal);
        }

        foreach (var code in new[] { "en", "de", "es", "fr", "ja", "ko" })
        {
            Assert.Contains("value=\"" + code + "\"", html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task UnknownBrowserLanguage_FallsBackToEnglish()
    {
        var html = await RenderAsync("", "zh-cn");

        Assert.Contains("Follow the browser (here: English)", html, StringComparison.Ordinal);
        Assert.Contains("Screen & Install", html, StringComparison.Ordinal);
    }

    private static string ShippedPackDir()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ProsimCompanion.slnx")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(
            directory?.FullName ?? throw new InvalidOperationException("Repo root not found above the test assembly."),
            "src", "ProsimCompanion.App", "config", "lang");
    }
}
