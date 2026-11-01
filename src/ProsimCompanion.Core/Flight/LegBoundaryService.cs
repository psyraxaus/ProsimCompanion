using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Diagnostics.Ecam;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Flight;

/// <summary>
/// Feeds <see cref="LegBoundaryCore"/> from the live edges and publishes the result
/// (ADR-0019 §2, issues #210 / #211): GSX deboarding complete (the ground-ops hub),
/// the FMS clear (<see cref="FmsPlanWatcher"/>, counted only parked with the engines off),
/// a plan landing in the <see cref="OfpStore"/> (identity by <see cref="OfpPlanIdentity"/>)
/// and a take-off (the boundary cannot outlive one). Every accepted edge is one
/// <c>leg.boundary</c> session event and one line on the Diagnostics RECENT list; an FMS
/// clear is also an <c>fms.cleared</c> event whether or not it counted. Degrade-not-fail:
/// with ProSim absent the FMS refs never change and the machine simply never leaves None.
/// </summary>
public sealed class LegBoundaryService : ILegBoundaryEvents, IDisposable
{
    private readonly GroundOpsSignals _signals;
    private readonly FmsPlanWatcher _fms;
    private readonly OfpStore _ofp;
    private readonly IFlightPhaseSource _flight;
    private readonly LegBoundaryStore _store;
    private readonly JsonlEventLog _eventLog;
    private readonly DiagEventLog _diagEvents;
    private readonly ILogger<LegBoundaryService> _logger;
    private readonly LegBoundaryCore _core = new();
    private readonly object _gate = new();

    public LegBoundaryService(
        GroundOpsSignals signals,
        FmsPlanWatcher fms,
        OfpStore ofp,
        IFlightPhaseSource flight,
        LegBoundaryStore store,
        JsonlEventLog eventLog,
        DiagEventLog diagEvents,
        ILogger<LegBoundaryService> logger)
    {
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(fms);
        ArgumentNullException.ThrowIfNull(ofp);
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(diagEvents);
        ArgumentNullException.ThrowIfNull(logger);
        _signals = signals;
        _fms = fms;
        _ofp = ofp;
        _flight = flight;
        _store = store;
        _eventLog = eventLog;
        _diagEvents = diagEvents;
        _logger = logger;

        // A plan already in the store (restored before this module woke) is the current leg.
        var current = OfpPlanIdentity.PlanId(_ofp.Current);
        if (current is not null)
        {
            lock (_gate)
            {
                _core.PlanLoaded(current, DateTimeOffset.UtcNow);
            }

            _store.Update(_ => _core.Snapshot);
        }

        _signals.ArrivalCompleted += OnArrivalCompleted;
        _fms.Cleared += OnFmsCleared;
        _ofp.Changed += OnOfpChanged;
        _flight.PhaseChanged += OnPhaseChanged;
    }

    public event EventHandler<LegBeganEventArgs>? LegBegan;

    private void OnArrivalCompleted()
    {
        LegBoundaryChange? change;
        lock (_gate)
        {
            change = _core.Deboarded(DateTimeOffset.UtcNow);
        }

        Publish(change);
    }

    private void OnFmsCleared(object? sender, FmsClearedEventArgs e)
    {
        var data = _flight.Snapshot().Data;
        var parked = data is { IsValid: true, OnGround: true, AnyEngineRunning: false } && data.GroundSpeedKt < 1;
        _eventLog.Record("fms.cleared", new
        {
            origin = e.PreviousOrigin,
            destination = e.PreviousDestination,
            fmgcPhase = e.FmgcPhase,
            parked,
            accepted = parked,
        });

        if (!parked)
        {
            // ADR-0019 §2: the clear counts "while parked with the engines off". Airborne or
            // rolling, the box emptying is something else (a re-entry, a test) — on the record,
            // not a boundary step.
            _logger.LogInformation(
                "FMS cleared while not parked with the engines off — recorded, not a leg-boundary step (phase {Phase})",
                _flight.CurrentPhase);
            return;
        }

        LegBoundaryChange? change;
        lock (_gate)
        {
            change = _core.FmsCleared(DateTimeOffset.UtcNow);
        }

        Publish(change);
    }

    private void OnOfpChanged(object? sender, EventArgs e)
    {
        var planId = OfpPlanIdentity.PlanId(_ofp.Current);
        LegBoundaryChange? change;
        lock (_gate)
        {
            change = _core.PlanLoaded(planId, DateTimeOffset.UtcNow);
        }

        Publish(change);
    }

    private void OnPhaseChanged(object? sender, FlightPhaseChangedEventArgs e)
    {
        if (e.Current is not (FlightPhase.TakeoffRoll or FlightPhase.InitialClimb or FlightPhase.Climb))
        {
            return;
        }

        LegBoundaryChange? change;
        lock (_gate)
        {
            change = _core.Airborne(DateTimeOffset.UtcNow);
        }

        Publish(change);
    }

    private void Publish(LegBoundaryChange? change)
    {
        if (change is null)
        {
            return;
        }

        var snapshot = _core.Snapshot;
        _store.Update(_ => snapshot);

        if (change.Warning is { } warning)
        {
            _logger.LogWarning("Leg boundary {From} → {To}: {Reason}; {Warning}", change.From, change.To, change.Reason, warning);
        }
        else
        {
            _logger.LogInformation("Leg boundary {From} → {To}: {Reason}", change.From, change.To, change.Reason);
        }

        _eventLog.Record("leg.boundary", new
        {
            state = change.To.ToString(),
            previous = change.From.ToString(),
            reason = change.Reason,
            warning = change.Warning,
            planId = change.PlanId,
            legBegan = change.LegBegan,
            deboarded = snapshot.Deboarded,
            fmsCleared = snapshot.FmsCleared,
        });

        _diagEvents.Push(new DiagEvent(
            DateTimeOffset.UtcNow,
            change.To switch
            {
                LegBoundaryState.Turnaround => "TURNAROUND",
                LegBoundaryState.AwaitingPlan => "AWAITING PLAN",
                _ => change.LegBegan ? "NEW LEG" : "LEG BOUNDARY CLEARED",
            },
            change.Warning is null ? DiagState.Normal : DiagState.Caution));

        if (change.LegBegan && change.PlanId is { } planId)
        {
            try
            {
                LegBegan?.Invoke(this, new LegBeganEventArgs(planId, change.PreviousPlanId, change.From));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "A leg-began subscriber failed");
            }
        }
    }

    public void Dispose()
    {
        _signals.ArrivalCompleted -= OnArrivalCompleted;
        _fms.Cleared -= OnFmsCleared;
        _ofp.Changed -= OnOfpChanged;
        _flight.PhaseChanged -= OnPhaseChanged;
    }
}
