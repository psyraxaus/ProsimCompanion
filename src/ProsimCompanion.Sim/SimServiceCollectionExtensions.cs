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
        services.AddSingleton<SimSessionSignals>();
        // Airport parkings from the simulator's facility data (Option B, 2026-10-04): the
        // scenery tier of Core's parking catalogue — identity, position, radius, jetway for
        // every stand, default (streamed) airports included. SimConnectService feeds it.
        services.AddSingleton<Facilities.AirportFacilityService>();
        services.AddSingleton<Core.Airports.Parking.IAirportParkingSource>(
            provider => provider.GetRequiredService<Facilities.AirportFacilityService>());
        services.AddHostedService<SimConnectService>();
        // Session detection (camera state + Sim/Pause_EX1 events): publishes SimSessionStore,
        // the gate that keeps GSX ground prep from firing while MSFS is on the main menu.
        services.AddHostedService<SimSessionService>();

        return services;
    }
}
