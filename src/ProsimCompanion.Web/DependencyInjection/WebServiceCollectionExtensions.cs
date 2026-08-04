using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
        return services;
    }
}
