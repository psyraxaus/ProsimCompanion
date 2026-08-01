using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.DependencyInjection;

public static class CoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers the domain services shared by every surface: option bindings from
    /// config/settings.json, the settings write path, and the observable state stores.
    /// </summary>
    public static IServiceCollection AddCoreServices(
        this IServiceCollection services,
        IConfiguration configuration,
        string settingsFilePath)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsFilePath);

        services.Configure<WebUiOptions>(configuration.GetSection(WebUiOptions.SectionName));
        services.Configure<ProsimOptions>(configuration.GetSection(ProsimOptions.SectionName));
        services.Configure<GsxOptions>(configuration.GetSection(GsxOptions.SectionName));

        services.AddSingleton(new JsonSettingsFile(settingsFilePath));
        services.AddSingleton<ConnectionStatusStore>();

        return services;
    }
}
