using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Hosting;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Sim;

public static class SimServiceCollectionExtensions
{
    /// <summary>
    /// Registers MSFS/SimConnect connectivity. Phase 1 replaces the placeholder with the real
    /// clean-room SimConnect + LVAR layer (ADR-0003; LVAR strategy per docs/integrations/gsx.md §5).
    /// </summary>
    public static IServiceCollection AddSimServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IHostedService>(provider => new PlaceholderSubsystemService(
            provider.GetRequiredService<ConnectionStatusStore>(),
            provider.GetRequiredService<ILogger<PlaceholderSubsystemService>>(),
            Subsystems.SimConnect,
            "Phase 1"));

        return services;
    }
}
