using Microsoft.Extensions.Logging;
using ProsimCompanion.Gsx.Mirror;
using ProsimCompanion.Gsx.Protocol;

namespace ProsimCompanion.Gsx.Services;

/// <summary>Once-per-cycle lifecycle notifications derived from mirror state edges.</summary>
public enum GsxServiceLifecycleEvent
{
    Requested,
    Active,
    Completed,
}

/// <summary>
/// Turns raw mirror service states into reliable once-per-cycle lifecycle events, implementing
/// the predecessor's hard-won edge rules (docs/integrations/gsx-remote-api.md §4.3):
///
/// - notifications latch — each of Requested/Active/Completed fires at most once per cycle;
/// - <b>return-to-available = completed</b>: quick services (Water/Lavatory/Cleaning, and
///   refuel-adjacent behaviour) go performing → available with no completed edge — `available`
///   after was-active counts as completed, or "after all services" gating never fires;
/// - a `completed` reading without an observed active phase still completes (the active edge was
///   missed — latch was-active first);
/// - re-running the same states is idempotent, which is exactly what the periodic reconcile tick
///   does to catch missed edges;
/// - a service vanishing from the mirror (aircraft/sim change) resets its cycle.
///
/// Driven from the WebSocket receive path; events fire on that thread.
/// </summary>
public sealed class GsxServiceLifecycleTracker
{
    private readonly ILogger<GsxServiceLifecycleTracker> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, ServiceCycle> _cycles = new(StringComparer.OrdinalIgnoreCase);

    public GsxServiceLifecycleTracker(ILogger<GsxServiceLifecycleTracker> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>Raised once per (service, event) per cycle, on the caller's thread.</summary>
    public event Action<string, GsxServiceLifecycleEvent>? ServiceEvent;

    /// <summary>Marks that the automation itself triggered this service — completion is then
    /// accepted even if the active phase was never observed.</summary>
    public void MarkCalled(string serviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);
        lock (_gate)
        {
            GetCycle(serviceId).WasCalled = true;
        }
    }

    /// <summary>True when the service has completed this cycle (explicitly or via the
    /// return-to-available rule).</summary>
    public bool IsCompleted(string serviceId)
    {
        lock (_gate)
        {
            return _cycles.TryGetValue(serviceId, out var cycle) && cycle.CompletedNotified;
        }
    }

    /// <summary>Processes the current mirror services. Call on every mirror services update AND
    /// from the periodic reconcile tick (idempotent by design).</summary>
    public void Process(IReadOnlyDictionary<string, GsxServiceInfo> services)
    {
        ArgumentNullException.ThrowIfNull(services);

        List<(string Id, GsxServiceLifecycleEvent Event)> toFire = [];

        lock (_gate)
        {
            // A service dropping out of the mirror means aircraft/sim change — reset its cycle.
            foreach (var known in _cycles.Keys.ToArray())
            {
                if (!services.ContainsKey(known))
                {
                    _cycles.Remove(known);
                }
            }

            foreach (var (id, info) in services)
            {
                var cycle = GetCycle(id);

                switch (info.State)
                {
                    case GsxServiceState.Requested when !cycle.RequestedNotified:
                        cycle.RequestedNotified = true;
                        toFire.Add((info.Id, GsxServiceLifecycleEvent.Requested));
                        break;

                    case GsxServiceState.Active when !cycle.ActiveNotified:
                        cycle.WasActive = true;
                        cycle.ActiveNotified = true;
                        toFire.Add((info.Id, GsxServiceLifecycleEvent.Active));
                        break;

                    case GsxServiceState.Completed when !cycle.CompletedNotified:
                        // A completed reading with no observed active phase means the active
                        // edge was missed — reconcile by latching was-active first.
                        cycle.WasActive = true;
                        cycle.CompletedNotified = true;
                        toFire.Add((info.Id, GsxServiceLifecycleEvent.Completed));
                        break;

                    case GsxServiceState.Callable
                        when cycle.WasActive && !cycle.CompletedNotified:
                        // Return-to-available = completed (quick services skip the completed edge).
                        cycle.CompletedNotified = true;
                        toFire.Add((info.Id, GsxServiceLifecycleEvent.Completed));
                        break;
                }
            }
        }

        foreach (var (id, lifecycleEvent) in toFire)
        {
            _logger.LogInformation("GSX service {Service}: {Event}", id, lifecycleEvent);
            try
            {
                ServiceEvent?.Invoke(id, lifecycleEvent);
            }
            catch (Exception ex)
            {
                // A consumer failure must never break edge processing for other services.
                _logger.LogError(ex, "GSX service lifecycle handler for {Service} threw", id);
            }
        }
    }

    /// <summary>Starts a fresh cycle for every service (new turnaround / departure), so the
    /// lifecycle events fire again.</summary>
    public void ResetCycle()
    {
        lock (_gate)
        {
            _cycles.Clear();
        }
        _logger.LogInformation("GSX service lifecycle cycle reset");
    }

    private ServiceCycle GetCycle(string serviceId)
    {
        if (!_cycles.TryGetValue(serviceId, out var cycle))
        {
            cycle = new ServiceCycle();
            _cycles[serviceId] = cycle;
        }
        return cycle;
    }

    private sealed class ServiceCycle
    {
        public bool WasCalled { get; set; }
        public bool WasActive { get; set; }
        public bool RequestedNotified { get; set; }
        public bool ActiveNotified { get; set; }
        public bool CompletedNotified { get; set; }
    }
}
