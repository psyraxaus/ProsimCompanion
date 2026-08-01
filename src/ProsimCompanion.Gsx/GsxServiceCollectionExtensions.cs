using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Hosting;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Gsx;

public static class GsxServiceCollectionExtensions
{
    /// <summary>
    /// Registers GSX Pro ground automation. Phase 2 replaces the placeholder with the Couatl
    /// Remote API v2 client, menu intent framework, and the ground automation state machine
    /// (docs/integrations/gsx.md).
    /// </summary>
    public static IServiceCollection AddGsxServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IHostedService>(provider => new PlaceholderSubsystemService(
            provider.GetRequiredService<ConnectionStatusStore>(),
            provider.GetRequiredService<ILogger<PlaceholderSubsystemService>>(),
            Subsystems.Gsx,
            "Phase 2"));

        return services;
    }
}
