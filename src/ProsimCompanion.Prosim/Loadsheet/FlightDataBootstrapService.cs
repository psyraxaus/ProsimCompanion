using Microsoft.Extensions.Hosting;

namespace ProsimCompanion.Prosim.Loadsheet;

/// <summary>
/// Activates the event-driven flight-data singletons at startup (the same
/// injected-purely-for-wiring pattern the GSX bootstrap uses) — without this, the DI container
/// would never construct them and no trigger would ever fire.
/// </summary>
public sealed class FlightDataBootstrapService : IHostedService
{
    public FlightDataBootstrapService(LoadsheetService loadsheets, FmsInitSyncService fmsSync)
    {
        ArgumentNullException.ThrowIfNull(loadsheets);
        ArgumentNullException.ThrowIfNull(fmsSync);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
