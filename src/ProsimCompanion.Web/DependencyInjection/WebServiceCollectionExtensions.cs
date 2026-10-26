using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Hosting;
using ProsimCompanion.Core.Localization;
using ProsimCompanion.Web.Localization;
using ProsimCompanion.Web.Components;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Theming;
using ProsimCompanion.Web.Theming;

namespace ProsimCompanion.Web;

/// <summary>Registers the web UI's own services (per-project composition convention).</summary>
public static class WebServiceCollectionExtensions
{
    /// <param name="services">The service collection.</param>
    /// <param name="userThemesDirectory">Directory scanned for user theme JSON files
    /// (conventionally <c>config/themes</c> beside the settings file).</param>
    /// <param name="languagePackDirectories">UI language pack folders, highest priority
    /// first: the seeded user copies, then the shipped defaults (ADR-0014).</param>
    public static IServiceCollection AddWebServices(
        this IServiceCollection services,
        string userThemesDirectory,
        IReadOnlyList<string> languagePackDirectories)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(userThemesDirectory);
        ArgumentNullException.ThrowIfNull(languagePackDirectories);

        services.AddSingleton(provider => new ThemeCatalog(
            userThemesDirectory,
            provider.GetRequiredService<ILogger<ThemeCatalog>>()));
        // UI language packs (ADR-0014): one catalog, the configured language as a live store,
        // and a per-circuit localizer that also knows this browser's own language list.
        services.AddSingleton(provider => new LanguagePackCatalog(
            languagePackDirectories,
            provider.GetRequiredService<ILogger<LanguagePackCatalog>>()));
        services.AddSingleton<UiLanguageStore>();
        services.AddScoped<BrowserLanguageContext>();
        services.AddScoped<IStringLocalizer, UiText>();
        // The pilot's own airline logos (owner decision 2026-09-20: never shipped, never seeded).
        services.AddSingleton(_ => new ThemeLogoStore(UserConfigPaths.ThemeLogos));
        // Airline logos keyed by the OFP airline code for the pop-out Flight Monitor (2026-09-23).
        services.AddSingleton(_ => new AirlineLogoStore(UserConfigPaths.AirlineLogos));
        // Live mirror of webUi.showAdvancedSettings for the settings pages (ADR-0010).
        services.AddSingleton<AdvancedSettingsStore>();
        // Flight Status pill/text change log (issue #110) — runs whether or not a browser is open.
        services.AddStartupModule<FlightStatusChangeLog>();
        // Diagnostics REMOTE page (ADR-0016): one CircuitHandler per circuit records address,
        // browser class, route and a 5 s ping into the shared registry; the collector draws it.
        services.AddHttpContextAccessor();
        services.AddSingleton<Diagnostics.CircuitRegistry>();
        services.AddScoped<Microsoft.AspNetCore.Components.Server.Circuits.CircuitHandler, Diagnostics.CircuitTracker>();
        services.AddSingleton<Core.Diagnostics.Ecam.IDiagnosticsCollector, Diagnostics.WebHostDiagnosticsCollector>();
        return services;
    }
}
