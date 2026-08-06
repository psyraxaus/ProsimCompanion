using Microsoft.Extensions.DependencyInjection;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Aircraft.Gateway;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Prosim.DataRefs;
using ProsimCompanion.Prosim.Flight;
using ProsimCompanion.Prosim.Gateway;

namespace ProsimCompanion.Prosim;

public static class ProsimServiceCollectionExtensions
{
    /// <summary>
    /// Registers ProSim connectivity: the push-subscription dataref service (the app-wide
    /// <see cref="IProsimDataRefs"/> seam) and the SDK connection lifecycle. The EFB gateway
    /// client (GraphQL/REST on :5000) is a separate Phase 1 work item.
    /// </summary>
    public static IServiceCollection AddProsimServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<ProsimDataRefService>();
        services.AddSingleton<IProsimDataRefs>(provider => provider.GetRequiredService<ProsimDataRefService>());
        services.AddSingleton<IProsimGateway, ProsimGatewayClient>();
        services.AddSingleton<ISimbriefImporter, Simbrief.SimbriefImportService>();
        services.AddSingleton<IFlightDataSource, ProsimFlightDataSource>();
        services.AddHostedService<ProsimConnectionService>();

        // Flight-data pillar (Phase 3): in-house loadsheets + ACARS + MCDU INIT B sync.
        services.AddSingleton<Acars.AcarsUplink>();
        services.AddSingleton<Loadsheet.FmsInitSyncService>();
        services.AddSingleton<Core.State.IFmsInitSync>(provider => provider.GetRequiredService<Loadsheet.FmsInitSyncService>());
        services.AddSingleton<Loadsheet.FmsPerfUplinkService>();
        services.AddSingleton<Core.State.IFmsPerfUplink>(provider => provider.GetRequiredService<Loadsheet.FmsPerfUplinkService>());
        services.AddSingleton<Loadsheet.EfbInitOverridesService>();
        services.AddSingleton<Core.Aircraft.IEfbInitOverrides>(provider => provider.GetRequiredService<Loadsheet.EfbInitOverridesService>());
        services.AddSingleton<Loadsheet.LoadsheetService>();
        services.AddSingleton<Core.State.ILoadsheetControl>(provider => provider.GetRequiredService<Loadsheet.LoadsheetService>());
        services.AddHostedService<Loadsheet.FlightDataBootstrapService>();

        return services;
    }
}
