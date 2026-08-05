using ProsimCompanion.Core.State;
using ProsimCompanion.Gsx.Services;

namespace ProsimCompanion.Gsx.Sync;

/// <summary>
/// Publishes GSX service lifecycle edges onto the Core <see cref="GroundOpsSignals"/> hub for
/// other pillars (the loadsheet pipeline's prelim/final triggers). Deliberately independent of
/// the refuel/boarding sync modules and their enable flags — a loadsheet is wanted even when
/// fuel/pax syncing is turned off.
/// </summary>
public sealed class GsxGroundOpsSignalRelay : IDisposable
{
    private readonly GsxServiceLifecycleTracker _lifecycle;
    private readonly GroundOpsSignals _signals;

    public GsxGroundOpsSignalRelay(GsxServiceLifecycleTracker lifecycle, GroundOpsSignals signals)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(signals);
        _lifecycle = lifecycle;
        _signals = signals;
        _lifecycle.ServiceEvent += OnServiceEvent;
    }

    public void Dispose() => _lifecycle.ServiceEvent -= OnServiceEvent;

    private void OnServiceEvent(string serviceId, GsxServiceLifecycleEvent lifecycleEvent)
    {
        if (serviceId.Equals("Refueling", StringComparison.OrdinalIgnoreCase)
            && lifecycleEvent == GsxServiceLifecycleEvent.Active)
        {
            _signals.RaiseRefuelServiceActive();
        }
        else if (serviceId.Equals("Boarding", StringComparison.OrdinalIgnoreCase)
            && lifecycleEvent == GsxServiceLifecycleEvent.Completed)
        {
            _signals.RaiseBoardingCompleted();
        }
    }
}
