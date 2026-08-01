using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Gsx.Services;

namespace ProsimCompanion.Gsx;

/// <summary>
/// Wires the GSX layers together: mirror service updates feed the lifecycle tracker; a periodic
/// reconcile tick re-processes current state to catch missed edges; a Couatl restart (sid
/// change) is surfaced; lifecycle events are recorded in the session event log.
/// </summary>
public sealed class GsxBootstrapService : IHostedService, IDisposable
{
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(5);

    private readonly GsxRemoteApiClient _client;
    private readonly GsxServiceLifecycleTracker _lifecycle;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<GsxBootstrapService> _logger;
    private Timer? _reconcileTimer;

    public GsxBootstrapService(
        GsxRemoteApiClient client,
        GsxServiceLifecycleTracker lifecycle,
        JsonlEventLog eventLog,
        ILogger<GsxBootstrapService> logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _client = client;
        _lifecycle = lifecycle;
        _eventLog = eventLog;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _client.Mirror.Updated += OnMirrorUpdated;
        _client.Mirror.SidChanged += OnSidChanged;
        _lifecycle.ServiceEvent += OnServiceEvent;

        _reconcileTimer = new Timer(
            _ => _lifecycle.Process(_client.Mirror.Services),
            null,
            ReconcileInterval,
            ReconcileInterval);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _client.Mirror.Updated -= OnMirrorUpdated;
        _client.Mirror.SidChanged -= OnSidChanged;
        _lifecycle.ServiceEvent -= OnServiceEvent;
        _reconcileTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        return Task.CompletedTask;
    }

    public void Dispose() => _reconcileTimer?.Dispose();

    private void OnMirrorUpdated(string key)
    {
        if (string.Equals(key, "services", StringComparison.OrdinalIgnoreCase))
        {
            _lifecycle.Process(_client.Mirror.Services);
        }
    }

    private void OnSidChanged(string? oldSid, string? newSid)
    {
        // Engine restart: cached context is gone. Service cycles keep their latches — the next
        // snapshot re-syncs states and the idempotent processing avoids duplicate events.
        _logger.LogWarning("Couatl engine restarted (sid {Old} -> {New}); cached GSX context invalidated", oldSid, newSid);
        _eventLog.Record("gsx-engine-restarted", new { oldSid, newSid });
    }

    private void OnServiceEvent(string serviceId, GsxServiceLifecycleEvent lifecycleEvent)
        => _eventLog.Record("gsx-service", new { service = serviceId, @event = lifecycleEvent.ToString() });
}
