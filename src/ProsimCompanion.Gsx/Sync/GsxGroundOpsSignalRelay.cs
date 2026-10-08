using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.State;
using ProsimCompanion.Gsx.Services;

namespace ProsimCompanion.Gsx.Sync;

/// <summary>
/// Publishes GSX service lifecycle edges onto the Core <see cref="GroundOpsSignals"/> hub for
/// other pillars (the loadsheet pipeline's prelim/final triggers, the deice holdover card,
/// the outbound notifications). Deliberately independent of the refuel/boarding sync modules
/// and their enable flags — a loadsheet is wanted even when fuel/pax syncing is turned off.
/// <para>Once per cycle is enforced HERE, by signal (issue #90): the tracker's per-service
/// latches reset when a service drops out of the mirror (Couatl restart, aircraft swap) and
/// the de-ice match is a substring over the id, so the tracker alone can hand this relay the
/// same milestone twice in one turnaround. The memory is the pure
/// <see cref="GroundOpsSignalRelayCore"/> and clears only on the hub's own
/// <see cref="GroundOpsSignals.FlightCycleReset"/> — the GSX arrival reset — which is also
/// what every milestone consumer re-arms on, so relay and consumers agree on the cycle.</para>
/// </summary>
public sealed class GsxGroundOpsSignalRelay : IDisposable
{
    private readonly GsxServiceLifecycleTracker _lifecycle;
    private readonly GroundOpsSignals _signals;
    private readonly ILogger<GsxGroundOpsSignalRelay> _logger;
    private readonly IDataRefSubscription<double> _deiceFluidType;
    private readonly GroundOpsSignalRelayCore _core = new();
    private readonly object _gate = new();

    public GsxGroundOpsSignalRelay(
        GsxServiceLifecycleTracker lifecycle,
        GroundOpsSignals signals,
        ISimVars simVars,
        ILogger<GsxGroundOpsSignalRelay> logger)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(simVars);
        ArgumentNullException.ThrowIfNull(logger);
        _lifecycle = lifecycle;
        _signals = signals;
        _logger = logger;
        // Applied fluid TYPE the user picked at the GSX deice crew (1=Type I … 4=Type IV).
        // GSX exposes the type but not the concentration.
        _deiceFluidType = simVars.Subscribe(GsxLvarNames.DeicingType);
        _lifecycle.ServiceEvent += OnServiceEvent;
        _signals.FlightCycleReset += OnFlightCycleReset;
    }

    public void Dispose()
    {
        _lifecycle.ServiceEvent -= OnServiceEvent;
        _signals.FlightCycleReset -= OnFlightCycleReset;
        _deiceFluidType.Dispose();
    }

    private void OnFlightCycleReset()
    {
        lock (_gate)
        {
            _core.Reset();
        }
    }

    private void OnServiceEvent(string serviceId, GsxServiceLifecycleEvent lifecycleEvent)
    {
        GroundOpsRelaySignal? signal;
        bool suppressed;
        lock (_gate)
        {
            signal = _core.TryTake(serviceId, lifecycleEvent, out suppressed);
        }

        if (suppressed)
        {
            // The leak paths of issue #90 land here: a mirror-vanish re-fire or a second
            // de-ice spelling. Logged so the flight report can count the absorbed edges.
            _logger.LogInformation(
                "GSX service {Service}: {Event} edge not relayed — its signal already went out this cycle (issue #90)",
                serviceId, lifecycleEvent);
            return;
        }

        switch (signal)
        {
            case GroundOpsRelaySignal.RefuelServiceActive:
                _signals.RaiseRefuelServiceActive();
                break;

            case GroundOpsRelaySignal.RefuelCompleted:
                _signals.RaiseRefuelCompleted(); // the outbound "refuel complete" (#151)
                break;

            case GroundOpsRelaySignal.BoardingStarted:
                // The phase engine's departure gate (owner decision 2026-09-20): Preflight
                // becomes Departure once the passengers start boarding.
                _signals.RaiseBoardingStarted();
                break;

            case GroundOpsRelaySignal.BoardingCompleted:
                _signals.RaiseBoardingCompleted();
                break;

            case GroundOpsRelaySignal.DeboardingCompleted:
                // The phase engine's turnaround gate (2026-08-29 ESSA): Shutdown may become
                // the next leg's Preflight only after the passengers are off.
                _signals.RaiseArrivalCompleted();
                break;

            case GroundOpsRelaySignal.DeiceCompleted:
                _signals.RaiseDeiceCompleted((int)_deiceFluidType.Value);
                break;
        }
    }
}
