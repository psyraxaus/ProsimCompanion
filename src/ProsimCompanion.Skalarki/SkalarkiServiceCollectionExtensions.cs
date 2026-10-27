using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ProsimCompanion.Skalarki;

public static class SkalarkiServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Skalarki hardware link (PANELS page, 2026-10-27): the SDK loader, the
    /// connection service that feeds the Core store, and the diagnostics collector. The options
    /// section and the store are registered by Core so the web pages work with this pillar
    /// absent. Spec: docs/integrations/skalarki.md, ADR-0017.
    /// </summary>
    public static IServiceCollection AddSkalarkiServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<ISkalarkiClientFactory, SdkLoader>();
        services.AddSingleton<Core.Hardware.Skalarki.ISkalarkiSdkInspector, SkalarkiProbe>();
        services.AddSingleton<SkalarkiConnectionService>();
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<SkalarkiConnectionService>());
        // The PANELS page's LAMP TEST / HW RESET / hardware check (package 2).
        services.AddSingleton<Core.Hardware.Skalarki.ISkalarkiControl>(provider => provider.GetRequiredService<SkalarkiConnectionService>());
        return services;
    }
}
