using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Day;
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
/// <para>
/// <b>Restart</b> (ADR-0019 §9, issue #220): the machine is the <c>turnaround</c> part of the
/// flight-state snapshot — the one part that <see cref="IFlightStatePart.PersistsAcrossLegs"/>,
/// written while a boundary is in progress and restored before the OFP part so the flown
/// plan's id is already known when the OFP comes back (no false "first plan" edge). With no
/// snapshot the GSX startup resync may hand in "deboarded" from its tracking LVARs
/// (<see cref="ILegBoundaryRecovery"/>). The end of duty (<see cref="DutyDaySignals"/>) puts
/// the machine back to None: no boundary is in progress once the day is closed.
/// </para>
/// </summary>
public sealed class LegBoundaryService : ILegBoundaryEvents, ILegBoundaryRecovery, IFlightStatePart, IDisposable
{
    public const string SnapshotPartName = "turnaround";
    public const string RestoredReason = "restored from the flight-state snapshot";

    private readonly GroundOpsSignals _signals;
    private readonly FmsPlanWatcher _fms;
    private readonly OfpStore _ofp;
    private readonly IFlightPhaseSource _flight;
    private readonly LegBoundaryStore _store;
    private readonly JsonlEventLog _eventLog;
    private readonly DiagEventLog _diagEvents;
    private readonly ILogger<LegBoundaryService> _logger;
    private readonly DutyDaySignals? _dutyDay;
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
        ILogger<LegBoundaryService> logger,
        DutyDaySignals? dutyDay = null)
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
        _dutyDay = dutyDay;

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
        if (_dutyDay is not null)
        {
            _dutyDay.DayEnded += OnDutyDayEnded;
        }
    }

    public event EventHandler<LegBeganEventArgs>? LegBegan;

    // ---- the flight-state snapshot part (ADR-0019 §9, issue #220) -----------------------------

    /// <inheritdoc />
    public string Key => SnapshotPartName;

    /// <summary>Before the OFP part (10): the flown plan's id must be in the machine when the
    /// OFP store fires, or the restored OFP reads as the first plan of a new leg.</summary>
    public int RestoreOrder => 5;

    /// <inheritdoc />
    public bool PersistsAcrossLegs => true;

    /// <inheritdoc />
    public event Action? Changed;

    /// <inheritdoc />
    public JsonNode? Capture()
    {
        LegBoundarySnapshot snapshot;
        lock (_gate)
        {
            snapshot = _core.Snapshot;
        }

        // Nothing between legs → nothing to come back to; a plain "a leg is planned" state
        // is the OFP part's business.
        return snapshot is { State: LegBoundaryState.None, Deboarded: false, FmsCleared: false }
            ? null
            : JsonSerializer.SerializeToNode(snapshot, FlightStateSnapshotStore.JsonOptions);
    }

    /// <inheritdoc />
    public void Restore(JsonNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var saved = node.Deserialize<LegBoundarySnapshot>(FlightStateSnapshotStore.JsonOptions);
        if (saved is null)
        {
            return;
        }

        LegBoundaryState before;
        lock (_gate)
        {
            before = _core.Snapshot.State;
            _core.Restore(saved with { Reason = RestoredReason });
        }

        PublishAdopted(before, saved.State, RestoredReason, saved.PlanId);
        _logger.LogInformation(
            "Leg boundary restored from the flight-state snapshot: {State} (deboarded {Deboarded}, FMS cleared {FmsCleared}, flown plan {PlanId})",
            saved.State, saved.Deboarded, saved.FmsCleared, saved.PlanId ?? "-");
    }

    /// <inheritdoc />
    public bool AdoptDeboardedFromEvidence(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        LegBoundaryChange? change;
        lock (_gate)
        {
            if (_core.Snapshot is not { State: LegBoundaryState.None, Deboarded: false, FmsCleared: false })
            {
                return false; // a snapshot or a live edge already said where we are
            }

            change = _core.Deboarded(DateTimeOffset.UtcNow);
            if (change is not null)
            {
                _core.Restore(_core.Snapshot with { Reason = "deboarding complete — " + source });
            }
        }

        if (change is null)
        {
            return false;
        }

        Publish(change with { Reason = "deboarding complete — " + source, Warning = null });
        return true;
    }

    private void OnDutyDayEnded(DutyDayEnded ended)
    {
        LegBoundaryState before;
        string? planId;
        lock (_gate)
        {
            before = _core.Snapshot.State;
            planId = _core.Snapshot.PlanId;
            if (_core.Snapshot is { State: LegBoundaryState.None, Deboarded: false, FmsCleared: false })
            {
                return;
            }

            // The state and the step flags go; the flown plan's id stays as the baseline —
            // "the same OFP loaded twice is one leg" (ADR-0019 §2) holds across the end of duty
            // too. Dropping it made a forced re-import of the flown plan read as "first plan of
            // the session" → LegBegan → a new day on the flown plan (review 2026-11-05).
            _core.Restore(LegBoundarySnapshot.Empty with { PlanId = planId, ChangedAtUtc = DateTimeOffset.UtcNow, Reason = "end of duty" });
        }

        PublishAdopted(before, LegBoundaryState.None, $"end of duty ({ended.Source}) — no boundary in progress", planId);
    }

    // ---- the live edges --------------------------------------------------------------------------

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
        // With SimConnect down and ProSim up the data sample is not valid, but the IOS Reset
        // FMS is still the pilot's step: the committed phase stands in for "parked with the
        // engines off" (review 2026-11-05 — the chip never left TURNAROUND on such a rig).
        var parkedBy = data is { IsValid: true }
            ? (data is { OnGround: true, AnyEngineRunning: false } && data.GroundSpeedKt < 1 ? "data" : null)
            : (_flight.CurrentPhase is FlightPhase.ColdAndDark or FlightPhase.Preflight or FlightPhase.Departure or FlightPhase.Shutdown ? "phase" : null);
        var parked = parkedBy is not null;
        _eventLog.Record("fms.cleared", new
        {
            origin = e.PreviousOrigin,
            destination = e.PreviousDestination,
            fmgcPhase = e.FmgcPhase,
            parked,
            parkedBy,
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

    // ---- publishing ------------------------------------------------------------------------------

    /// <summary>A state adopted from outside the live edges (a snapshot, the end of duty): the
    /// store, the record and the diagnostics line, never the leg-began edge.</summary>
    private void PublishAdopted(LegBoundaryState from, LegBoundaryState to, string reason, string? planId)
    {
        var snapshot = _core.Snapshot;
        _store.Update(_ => snapshot);
        _logger.LogInformation("Leg boundary {From} → {To}: {Reason}", from, to, reason);
        _eventLog.Record("leg.boundary", new
        {
            state = to.ToString(),
            previous = from.ToString(),
            reason,
            warning = (string?)null,
            planId,
            legBegan = false,
            deboarded = snapshot.Deboarded,
            fmsCleared = snapshot.FmsCleared,
        });
        _diagEvents.Push(new DiagEvent(
            DateTimeOffset.UtcNow,
            to switch
            {
                LegBoundaryState.Turnaround => "TURNAROUND",
                LegBoundaryState.AwaitingPlan => "AWAITING PLAN",
                _ => "LEG BOUNDARY CLEARED",
            },
            DiagState.Normal));
        Changed?.Invoke();
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

        Changed?.Invoke();

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
        if (_dutyDay is not null)
        {
            _dutyDay.DayEnded -= OnDutyDayEnded;
        }
    }
}
