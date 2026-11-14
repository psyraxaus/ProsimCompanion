using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Deice;
using ProsimCompanion.Core.EventLog;
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
    private readonly DeiceActivityStore _activity;
    private readonly JsonlEventLog _eventLog;
    private readonly IDataRefSubscription<double> _deiceFluidType;
    private readonly GroundOpsSignalRelayCore _core = new();
    private readonly object _gate = new();

    public GsxGroundOpsSignalRelay(
        GsxServiceLifecycleTracker lifecycle,
        GroundOpsSignals signals,
        ISimVars simVars,
        DeiceActivityStore activity,
        JsonlEventLog eventLog,
        ILogger<GsxGroundOpsSignalRelay> logger)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(simVars);
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);
        _lifecycle = lifecycle;
        _signals = signals;
        _activity = activity;
        _eventLog = eventLog;
        _logger = logger;
        // The fluid TYPE GSX sprayed, as a zero-based index as far as two flights show
        // (0 = Type I … 3 = Type IV — DeiceAppliedFluid.FromLvar; until 2026-11-14 it was read
        // as 1 = Type I … 4 = Type IV). GSX exposes the type but not the concentration.
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

    private void OnFlightCycleReset(FlightCycleResetKind _)
    {
        // Arrival and new-plan resets alike (ADR-0019 §6): per-leg state, it starts over either way.
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
                _signals.RaiseDeiceCompleted(ResolveDeiceFluid());
                break;
        }
    }

    /// <summary>The fluid of the treatment that just completed (issue #166, EGLL 2026-11-14:
    /// the raw LVAR went out, read 3 after the app had picked "Fluid Type IV", and the card
    /// and the ground crew both said Type III). The app's own menu answer is the truth; the
    /// LVAR is compared with it, or — when the app did not answer the menu — read as a
    /// zero-based index. Once per treatment: this edge is once per cycle (the core above).</summary>
    private int ResolveDeiceFluid()
    {
        var menuFluidType = _activity.Snapshot().MenuFluidType;
        var fluid = DeiceAppliedFluid.Resolve(_deiceFluidType.RawValue is null ? null : _deiceFluidType.Value, menuFluidType);
        var lvarReading = fluid.LvarRaw is { } raw ? raw.ToString(System.Globalization.CultureInfo.InvariantCulture) : "nothing";
        var lvarLabel = DeiceAppliedFluid.Label(fluid.LvarFluidType ?? 0);

        switch (fluid.Witness)
        {
            case DeiceFluidWitness.MenuAnswer when fluid.Disagrees:
                _logger.LogWarning(
                    "De-icing fluid: the app answered {MenuFluid} on GSX's fluid menu but L:FSDT_GSX_DEICING_TYPE reads {Lvar} ({LvarFluid} as a zero-based index, 0 = Type I … 3 = Type IV — an inferred mapping) — the menu answer is used",
                    DeiceAppliedFluid.Label(fluid.FluidType), lvarReading, lvarLabel);
                break;

            case DeiceFluidWitness.MenuAnswer:
                _logger.LogInformation(
                    "De-icing fluid: {MenuFluid}, as the app answered GSX's fluid menu (L:FSDT_GSX_DEICING_TYPE reads {Lvar}; as a zero-based index that is the same fluid, or the LVAR was not delivered)",
                    DeiceAppliedFluid.Label(fluid.FluidType), lvarReading);
                break;

            case DeiceFluidWitness.LvarZeroBased:
                _logger.LogInformation(
                    "De-icing fluid: {Fluid} — the fluid menu was not answered by the app; L:FSDT_GSX_DEICING_TYPE reads {Lvar}, taken as a zero-based index (0 = Type I … 3 = Type IV): an assumption from the flights of 2026-10-10 and 2026-11-14, not documented by GSX",
                    DeiceAppliedFluid.Label(fluid.FluidType), lvarReading);
                break;

            default:
                _logger.LogInformation(
                    "De-icing fluid: unknown — the fluid menu was not answered by the app and L:FSDT_GSX_DEICING_TYPE reads {Lvar}",
                    lvarReading);
                break;
        }

        _eventLog.Record("gsx-deice-fluid-applied", new
        {
            fluidType = fluid.FluidType,
            source = fluid.Witness switch
            {
                DeiceFluidWitness.MenuAnswer => "menu answer",
                DeiceFluidWitness.LvarZeroBased => "lvar (zero-based, assumed)",
                _ => "unknown",
            },
            menuFluidType,
            lvar = fluid.LvarRaw,
            lvarFluidType = fluid.LvarFluidType,
            disagrees = fluid.Disagrees,
        });

        return fluid.FluidType;
    }
}
