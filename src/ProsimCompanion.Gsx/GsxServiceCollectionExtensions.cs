using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ProsimCompanion.Gsx;

public static class GsxServiceCollectionExtensions
{
    /// <summary>
    /// Registers GSX Pro connectivity: the Couatl Remote API v2 client (and, as Phase 2
    /// progresses, the menu intent framework, gate selection and the ground automation state
    /// machine on top of it). Spec: docs/integrations/gsx-remote-api.md.
    /// </summary>
    public static IServiceCollection AddGsxServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<GsxRemoteApiClient>();
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<GsxRemoteApiClient>());

        return services;
    }
}
