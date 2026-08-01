using Microsoft.Extensions.DependencyInjection;
using ProsimCompanion.Core.Aircraft;

namespace ProsimCompanion.Sim;

public static class SimServiceCollectionExtensions
{
    /// <summary>
    /// Registers MSFS connectivity: the SimVar read model (<see cref="ISimVars"/>) and the
    /// SimConnect session lifecycle. LVAR transport is a Phase 2 work item
    /// (docs/integrations/gsx.md §5).
    /// </summary>
    public static IServiceCollection AddSimServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<SimVarService>();
        services.AddSingleton<ISimVars>(provider => provider.GetRequiredService<SimVarService>());
        services.AddHostedService<SimConnectService>();

        return services;
    }
}
