using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Diagnostics.Ecam;
using ProsimCompanion.Core.Profiles;
using ProsimCompanion.Core.State;
using ProsimCompanion.Core.Tests.Diagnostics.Ecam;
using ProsimCompanion.Core.Tests.Localization;
using ProsimCompanion.Core.Theming;
using ProsimCompanion.Core.VoiceReference;
using ProsimCompanion.Web.Theming;
using Xunit;

namespace ProsimCompanion.Core.Tests.Web;

/// <summary>The app shell's Diagnostics nav item: the badge appears only while cautions are
/// up, turns red with a warning, and the link itself is always there.</summary>
public sealed class MainLayoutBadgeRenderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pc-layout-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private async Task<string> RenderAsync(DiagnosticsSnapshot snapshot)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IOptionsMonitor<WebUiOptions>>(new FixedOptionsMonitor<WebUiOptions>(new WebUiOptions { Language = "en" }));
        services.AddTestLocalizer();
        services.AddSingleton<NavigationManager>(new TestNavigationManager("http://localhost/"));
        services.AddSingleton<ConnectionStatusStore>();
        services.AddSingleton<SpeechStatusStore>();
        services.AddSingleton<Core.Aircraft.IProsimDataRefs>(new TableDataRefs());
        services.AddSingleton<UpdateStore>();
        services.AddSingleton(new ThemeCatalog(Path.Combine(_dir, "themes"), NullLogger<ThemeCatalog>.Instance));
        services.AddSingleton(new ThemeLogoStore(Path.Combine(_dir, "logos")));
        services.AddSingleton<IOptionsMonitor<AircraftProfilesOptions>>(new FixedOptionsMonitor<AircraftProfilesOptions>(new AircraftProfilesOptions()));
        services.AddSingleton<OfpStore>();
        services.AddSingleton<AircraftProfileService>();
        services.AddSingleton<GsxDiagnosticsStore>();
        services.AddSingleton(Mock.Of<IGsxDepartureControl>());
        services.AddSingleton(Mock.Of<IVoiceReference>());
        var store = new DiagnosticsStore();
        store.Publish(snapshot);
        services.AddSingleton(store);

        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, NullLoggerFactory.Instance);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<ProsimCompanion.Web.Layout.MainLayout>(ParameterView.Empty);
            return System.Net.WebUtility.HtmlDecode(output.ToHtmlString());
        });
    }

    [Fact]
    public async Task Badge_Hidden_WhenNothingIsUp()
    {
        var html = await RenderAsync(DiagnosticsSnapshot.Empty);
        Assert.Contains("href=\"diagnostics\"", html, StringComparison.Ordinal);
        Assert.Contains("Diagnostics", html, StringComparison.Ordinal);
        Assert.DoesNotContain("side-badge", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Badge_AmberCount_WithCautions_RedWithAWarning()
    {
        var amber = DiagnosticsSnapshot.Empty with { Memo = DiagnosticsSnapshot.Empty.Memo with { CautionCount = 2 } };
        var html = await RenderAsync(amber);
        Assert.Contains("side-badge", html, StringComparison.Ordinal);
        Assert.Contains(">2<", html, StringComparison.Ordinal);
        Assert.DoesNotContain("side-badge is-warning", html, StringComparison.Ordinal);

        var red = DiagnosticsSnapshot.Empty with { Memo = DiagnosticsSnapshot.Empty.Memo with { CautionCount = 1, WarningCount = 1 } };
        Assert.Contains("side-badge is-warning", await RenderAsync(red), StringComparison.Ordinal);
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager(string uri) => Initialize("http://localhost/", uri);

        protected override void NavigateToCore(string uri, bool forceLoad) => Uri = ToAbsoluteUri(uri).ToString();
    }
}
