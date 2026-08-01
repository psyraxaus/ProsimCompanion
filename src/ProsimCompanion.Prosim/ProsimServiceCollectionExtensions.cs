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

        return services;
    }
}
