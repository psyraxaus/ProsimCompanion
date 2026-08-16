using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.State;
using ProsimCompanion.Gsx.Services;

namespace ProsimCompanion.Gsx.Sync;

/// <summary>
/// Publishes GSX service lifecycle edges onto the Core <see cref="GroundOpsSignals"/> hub for
/// other pillars (the loadsheet pipeline's prelim/final triggers, the deice holdover card).
/// Deliberately independent of the refuel/boarding sync modules and their enable flags — a
/// loadsheet is wanted even when fuel/pax syncing is turned off.
/// </summary>
public sealed class GsxGroundOpsSignalRelay : IDisposable
{
    private readonly GsxServiceLifecycleTracker _lifecycle;
    private readonly GroundOpsSignals _signals;
    private readonly IDataRefSubscription<double> _deiceFluidType;

    public GsxGroundOpsSignalRelay(GsxServiceLifecycleTracker lifecycle, GroundOpsSignals signals, ISimVars simVars)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(simVars);
        _lifecycle = lifecycle;
        _signals = signals;
        // Applied fluid TYPE the user picked at the GSX deice crew (1=Type I … 4=Type IV).
        // GSX exposes the type but not the concentration.
        _deiceFluidType = simVars.Subscribe(GsxLvarNames.DeicingType);
        _lifecycle.ServiceEvent += OnServiceEvent;
    }

    public void Dispose()
    {
        _lifecycle.ServiceEvent -= OnServiceEvent;
        _deiceFluidType.Dispose();
    }

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
        else if (lifecycleEvent == GsxServiceLifecycleEvent.Completed
            && serviceId.Replace("-", "").Contains("deic", StringComparison.OrdinalIgnoreCase))
        {
            // Service id tolerance: GSX has shipped "Deice"/"De-Ice"/"Deicing" spellings.
            _signals.RaiseDeiceCompleted((int)_deiceFluidType.Value);
        }
    }
}
