using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Hosting;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Prosim;

public static class ProsimServiceCollectionExtensions
{
    /// <summary>
    /// Registers ProSim connectivity. Phase 1 replaces the placeholder with the real services:
    /// runtime SDK loading, the register-once push read model, the EFB gateway client, and the
    /// allow-listed write path (docs/integrations/prosim.md).
    /// </summary>
    public static IServiceCollection AddProsimServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IHostedService>(provider => new PlaceholderSubsystemService(
            provider.GetRequiredService<ConnectionStatusStore>(),
            provider.GetRequiredService<ILogger<PlaceholderSubsystemService>>(),
            Subsystems.Prosim,
            "Phase 1"));

        return services;
    }
}
