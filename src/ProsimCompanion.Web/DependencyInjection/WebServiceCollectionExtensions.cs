using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Hosting;
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
    public static IServiceCollection AddWebServices(
        this IServiceCollection services,
        string userThemesDirectory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(userThemesDirectory);

        services.AddSingleton(provider => new ThemeCatalog(
            userThemesDirectory,
            provider.GetRequiredService<ILogger<ThemeCatalog>>()));
        // The pilot's own airline logos (owner decision 2026-09-20: never shipped, never seeded).
        services.AddSingleton(_ => new ThemeLogoStore(UserConfigPaths.ThemeLogos));
        // Airline logos keyed by the OFP airline code for the pop-out Flight Monitor (2026-09-23).
        services.AddSingleton(_ => new AirlineLogoStore(UserConfigPaths.AirlineLogos));
        // Live mirror of webUi.showAdvancedSettings for the settings pages (ADR-0010).
        services.AddSingleton<AdvancedSettingsStore>();
        // Flight Status pill/text change log (issue #110) — runs whether or not a browser is open.
        services.AddStartupModule<FlightStatusChangeLog>();
        return services;
    }
}
