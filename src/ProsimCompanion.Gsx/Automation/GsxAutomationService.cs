using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Gsx.Gate;
using ProsimCompanion.Gsx.Protocol;
using ProsimCompanion.Gsx.Services;

namespace ProsimCompanion.Gsx.Automation;

/// <summary>
/// The ground-automation coordinator: tracks the automation phase from the central flight state
/// engine, runs the departure service sequence (OFP-gated, cursor + per-service activation
/// rules — see <see cref="DepartureSequencer"/>), arms the configured arrival gate when
/// reaching flight, writes handler.set autoSelectOperator once per gate session, and resets
/// service cycles on arrival. Trigger dispatch is strictly one call in flight at a time,
/// confirmed against the state mirror before the next goes out — the trigger ack proves
/// nothing, and GSX silently drops rapid-fire requests (round-7 smoke test: five simultaneous
/// triggers, only the last service ran while the board showed the rest "Called" forever).
/// Every decision — including holds and skips — is recorded with its reason (decision log +
/// session event log), deduplicated so a stable state does not spam.
/// </summary>
public sealed class GsxAutomationService : IDisposable, IGsxDepartureControl, IGsxTriggerDispatcher
{
    private static readonly TimeSpan PumpInterval = TimeSpan.FromSeconds(3);

    private sealed record InFlightTrigger(string ServiceId, DateTimeOffset SentAt);

    private readonly IGsxRemoteApi _api;
    private readonly GsxServiceLifecycleTracker _lifecycle;
    private readonly GsxGateSelectionService _gateSelection;
    private readonly Sync.GsxGroundPrepCoordinator _groundPrep;
    private readonly FlightStateEngine _flightState;
    private readonly IOptionsMonitor<GsxOptions> _options;
    private readonly GsxDiagnosticsStore _diagnostics;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<GsxAutomationService> _logger;
    private readonly ISimVars _simVars;
    private readonly IDataRefSubscription _ofpImported;
    private readonly IDataRefSubscription _fmsOrigin;
    private readonly IDataRefSubscription _fmsDestination;
    private readonly IDataRefSubscription _bookedSeatString;
    private readonly IDataRefSubscription _intRadCpt;
    private readonly IDataRefSubscription _intRadFo;
    private readonly Timer _pumpTimer;
    private readonly SemaphoreSlim _pumpLock = new(1, 1);
    private readonly Dictionary<string, string> _lastReasonByAction = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _triggerAttempts = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _departureStarted;
    private volatile bool _departureComplete;
    private volatile bool _forceNext;
    private volatile bool _isTurnaround;
    private volatile InFlightTrigger? _inFlight;
    private bool _paxTargetArmed;
    private string? _autoSelectArmedKey;
    private DateTimeOffset _lastImportAttempt = DateTimeOffset.MinValue;

    private readonly ISimbriefImporter _simbrief;
    private readonly GroundOpsSignals _groundOpsSignals;
    private readonly OfpStore _ofpStore;
    private readonly GsxResyncState _resyncState;

    public GsxAutomationService(
        IGsxRemoteApi api,
        GsxServiceLifecycleTracker lifecycle,
        GsxGateSelectionService gateSelection,
        Sync.GsxGroundPrepCoordinator groundPrep,
        FlightStateEngine flightState,
        IProsimDataRefs prosim,
        ISimVars simVars,
        ISimbriefImporter simbrief,
        OfpStore ofpStore,
        IOptionsMonitor<GsxOptions> options,
        GsxDiagnosticsStore diagnostics,
        GroundOpsSignals groundOpsSignals,
        GsxResyncState resyncState,
        JsonlEventLog eventLog,
        ILogger<GsxAutomationService> logger)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(gateSelection);
        ArgumentNullException.ThrowIfNull(groundPrep);
        ArgumentNullException.ThrowIfNull(simbrief);
        ArgumentNullException.ThrowIfNull(flightState);
        ArgumentNullException.ThrowIfNull(simVars);
        ArgumentNullException.ThrowIfNull(groundOpsSignals);
        ArgumentNullException.ThrowIfNull(resyncState);
        _resyncState = resyncState;
        ArgumentNullException.ThrowIfNull(ofpStore);
        _ofpStore = ofpStore;
        _simbrief = simbrief;
        _simVars = simVars;
        _groundOpsSignals = groundOpsSignals;
        ArgumentNullException.ThrowIfNull(prosim);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _api = api;
        _lifecycle = lifecycle;
        _gateSelection = gateSelection;
        _groundPrep = groundPrep;
        _flightState = flightState;
        _options = options;
        _diagnostics = diagnostics;
        _eventLog = eventLog;
        _logger = logger;

        _ofpImported = prosim.Subscribe(ProsimDataRefNames.EfbSimbriefPlanImported, DataRefTier.Infrequent);
        _fmsOrigin = prosim.Subscribe(ProsimDataRefNames.FmsOrigin, DataRefTier.Infrequent);
        _fmsDestination = prosim.Subscribe(ProsimDataRefNames.FmsDestination, DataRefTier.Infrequent);
        _bookedSeatString = prosim.Subscribe(ProsimDataRefNames.PaxBookedString, DataRefTier.Infrequent);
        // The INT/RAD switches on both ACPs are the cockpit "smart button" (predecessor
        // semantics): flicking to INT (value 0) force-calls the next departure service.
        _intRadCpt = prosim.Subscribe(ProsimDataRefNames.IntRadCpt, DataRefTier.Frequent);
        _intRadFo = prosim.Subscribe(ProsimDataRefNames.IntRadFo, DataRefTier.Frequent);
        _intRadCpt.ValueChanged += OnIntRadChanged;
        _intRadFo.ValueChanged += OnIntRadChanged;

        _flightState.PhaseChanged += OnFlightPhaseChanged;
        _lifecycle.ServiceEvent += OnServiceEvent;
        _api.Mirror.Updated += OnMirrorUpdated;
        _resyncState.Assessed += OnResyncAssessed;
        _pumpTimer = new Timer(_ => Pump(), null, PumpInterval, PumpInterval);
    }

    /// <summary>The startup resync finished: adopt the recovered turnaround flag (the
    /// in-memory one only rises on an in-session arrival) and let the held sequencer run.</summary>
    private void OnResyncAssessed()
    {
        if (_resyncState.TurnaroundDetected && !_isTurnaround)
        {
            _isTurnaround = true;
            RecordDecision("turnaround", "recovered from tracking LVARs by the startup resync");
        }

        Pump();
    }

    /// <summary>Current automation phase (derived, never re-computed by features).</summary>
    public GsxAutomationPhase Phase { get; private set; } = GsxAutomationPhase.SessionStart;

    /// <summary>True once the departure sequence has been started (manually or automatically).</summary>
    public bool DepartureStarted => _departureStarted;

    /// <summary>True once every departure service completed or was skipped — the
    /// beacon-orchestrated pushback sequence arms on this.</summary>
    public bool DepartureComplete => _departureComplete;

    bool IGsxDepartureControl.Started => _departureStarted;

    bool IGsxDepartureControl.Complete => _departureComplete;

    void IGsxDepartureControl.Start() => StartDepartureServices();

    void IGsxDepartureControl.ForceNext() => ForceNextService("web");

    /// <summary>Starts the departure service sequence (idempotent).</summary>
    public void StartDepartureServices()
    {
        if (_departureStarted)
        {
            return;
        }

        _departureStarted = true;
        _departureComplete = false;
        RecordDecision("departure sequence", "started");
        Pump();
    }

    /// <summary>Single-shot force-next (INT/RAD / web button): the next evaluation bypasses the
    /// current step's activation rule — including Manual. Consumed by that evaluation whether or
    /// not anything could be called; the flight-plan gate is never bypassed.</summary>
    public void ForceNextService(string source)
    {
        if (!_departureStarted || _departureComplete)
        {
            RecordDecision("force next service", $"{source}: ignored — departure sequence not running");
            return;
        }

        _forceNext = true;
        RecordDecision("force next service", $"requested by {source}");
        Pump();
    }

    /// <summary>
    /// On-demand dispatch through the SAME single in-flight slot the departure sequencer uses
    /// (<see cref="IGsxTriggerDispatcher"/>). The slot is reserved under the pump lock so a
    /// concurrent sequencing evaluation can never dispatch alongside; a private watcher then
    /// confirms/times out the trigger against the mirror even in phases where the departure
    /// pump does not run its own resolve (e.g. a Deboarding call at arrival).
    /// </summary>
    public async Task<GsxTriggerDispatch> TryDispatchServiceTriggerAsync(
        string serviceId,
        string source,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);

        // Reserve under the pump lock — the sequencer checks the slot under the same lock, so
        // there is exactly one writer. A held lock means an evaluation is mid-flight; briefly
        // waiting is cheaper (and friendlier) than refusing.
        if (!await _pumpLock.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false))
        {
            return new(GsxTriggerDispatchStatus.Busy, _inFlight?.ServiceId);
        }

        try
        {
            if (_inFlight is { } inFlight)
            {
                return new(GsxTriggerDispatchStatus.Busy, inFlight.ServiceId);
            }

            _inFlight = new InFlightTrigger(serviceId, DateTimeOffset.UtcNow);
        }
        finally
        {
            _pumpLock.Release();
        }

        RecordDecision($"trigger {serviceId}", $"requested by {source}");
        var result = await _api.SendCommandAsync(
            "service.trigger",
            new JsonObject { ["service"] = serviceId },
            cancellationToken).ConfigureAwait(false);
        if (!result.Ok)
        {
            if (_inFlight?.ServiceId.Equals(serviceId, StringComparison.OrdinalIgnoreCase) == true)
            {
                _inFlight = null;
            }
            RecordDecision($"trigger {serviceId}", $"rejected ({result.Code})");
            return new(GsxTriggerDispatchStatus.Rejected, RejectCode: result.Code);
        }

        _ = WatchOnDemandTriggerAsync(serviceId);
        return new(GsxTriggerDispatchStatus.Dispatched);
    }

    /// <summary>Confirm-or-timeout watcher for on-demand triggers, mirroring
    /// <see cref="ResolveInFlightTrigger"/> semantics. Exits early when the departure pump's
    /// own resolve got there first (the slot no longer carries this service).</summary>
    private async Task WatchOnDemandTriggerAsync(string serviceId)
    {
        try
        {
            var deadline = DateTimeOffset.UtcNow
                + TimeSpan.FromMilliseconds(_options.CurrentValue.TriggerConfirmTimeoutMs);
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (_inFlight?.ServiceId.Equals(serviceId, StringComparison.OrdinalIgnoreCase) != true)
                {
                    return; // resolved (or replaced) by the departure pump — nothing left to own
                }

                var cycles = _lifecycle.SnapshotCycles();
                cycles.TryGetValue(serviceId, out var cycle);
                var mirrorState = _api.Mirror.Services.TryGetValue(serviceId, out var info)
                    ? info.State
                    : (GsxServiceState?)null;
                if (cycle.Requested || cycle.Active || cycle.Completed
                    || mirrorState is GsxServiceState.Requested or GsxServiceState.Active or GsxServiceState.Completed)
                {
                    _lifecycle.MarkCalled(serviceId);
                    if (_inFlight?.ServiceId.Equals(serviceId, StringComparison.OrdinalIgnoreCase) == true)
                    {
                        _inFlight = null;
                    }
                    RecordDecision(
                        $"trigger {serviceId}",
                        $"confirmed by GSX ({mirrorState?.ToString() ?? "lifecycle edge"})");
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            }

            if (_inFlight?.ServiceId.Equals(serviceId, StringComparison.OrdinalIgnoreCase) == true)
            {
                _inFlight = null;
                RecordDecision(
                    $"trigger {serviceId}",
                    "not picked up by GSX within the confirm window — the call was dropped; the slot is free again");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "On-demand trigger watcher for {Service} failed", serviceId);
        }
    }

    public void Dispose()
    {
        _flightState.PhaseChanged -= OnFlightPhaseChanged;
        _lifecycle.ServiceEvent -= OnServiceEvent;
        _api.Mirror.Updated -= OnMirrorUpdated;
        _resyncState.Assessed -= OnResyncAssessed;
        _intRadCpt.ValueChanged -= OnIntRadChanged;
        _intRadFo.ValueChanged -= OnIntRadChanged;
        _pumpTimer.Dispose();
        _ofpImported.Dispose();
        _fmsOrigin.Dispose();
        _fmsDestination.Dispose();
        _bookedSeatString.Dispose();
        _intRadCpt.Dispose();
        _intRadFo.Dispose();
        _pumpLock.Dispose();
    }

    /// <summary>Fires on every INT/RAD movement; value 0 = INT (pressed). Only consumed while
    /// the departure sequence is running on the ground — the switch is a real radio control in
    /// every other phase.</summary>
    private void OnIntRadChanged(object? sender, EventArgs e)
    {
        var subscription = (IDataRefSubscription)sender!;
        if (subscription.GetValue(1) != 0)
        {
            return;
        }

        if (Phase is GsxAutomationPhase.Preparation or GsxAutomationPhase.SessionStart
            && _departureStarted
            && !_departureComplete)
        {
            ForceNextService("INT/RAD");
        }
    }

    private void OnFlightPhaseChanged(object? sender, FlightPhaseChangedEventArgs e)
    {
        var phase = GsxAutomationPhaseMapper.Map(e.Current);
        if (phase == Phase)
        {
            return;
        }

        Phase = phase;
        RecordDecision("automation phase", phase.ToString());
        _eventLog.Record("gsx-automation-phase", new { phase = phase.ToString() });

        switch (phase)
        {
            case GsxAutomationPhase.Flight:
                ArmConfiguredArrivalGate();
                break;

            case GsxAutomationPhase.Arrival:
                // New turnaround coming: fresh service cycles, fresh departure sequence. From
                // here on this session's departures are turnarounds (TurnAround-constrained
                // services like Cleaning/Lavatory arm; FirstLeg-constrained ones stop).
                _lifecycle.ResetCycle();
                _departureStarted = false;
                _departureComplete = false;
                _paxTargetArmed = false;
                _isTurnaround = true;
                _forceNext = false;
                _inFlight = null;
                lock (_triggerAttempts)
                {
                    _triggerAttempts.Clear();
                }
                // Cross-feature cycle boundary: loadsheet caches/edition counters reset here.
                _groundOpsSignals.RaiseFlightCycleReset();
                RecordDecision("turnaround", "service cycles reset after arrival");
                break;
        }

        Pump();
    }

    private void OnServiceEvent(string serviceId, GsxServiceLifecycleEvent lifecycleEvent)
    {
        // Every edge matters now: Requested/Active confirm an in-flight trigger (unblocking the
        // next dispatch), Completed advances the sequence.
        Pump();
    }

    private void OnMirrorUpdated(string key)
    {
        if (string.Equals(key, "handlerData", StringComparison.OrdinalIgnoreCase))
        {
            _ = ArmAutoSelectOperatorAsync();
        }
        else if (string.Equals(key, "services", StringComparison.OrdinalIgnoreCase))
        {
            Pump();
        }
    }

    /// <summary>One sequencing evaluation. Serialized and re-entrant-safe; cheap when idle.</summary>
    private void Pump()
    {
        if (!_pumpLock.Wait(0))
        {
            return; // a pump is already running; state changes re-trigger us anyway
        }

        try
        {
            var options = _options.CurrentValue;
            if (!options.AutomationEnabled || _api.Readiness != GsxReadiness.Ready)
            {
                return;
            }

            // Never sequence before the startup resync has assessed prior progress (#30) —
            // the assessment is terminal (it times out if GSX/ProSim never come up), so this
            // hold is bounded.
            if (!_resyncState.IsAssessed)
            {
                PublishWaitingBoard("waiting for the startup state resync");
                return;
            }

            if (!_departureStarted
                && options.AutoStartDepartureServices
                && Phase == GsxAutomationPhase.Preparation)
            {
                _departureStarted = true;
                RecordDecision("departure sequence", "auto-started (Preparation phase)");
            }

            if (!_departureStarted || _departureComplete
                || Phase is not (GsxAutomationPhase.Preparation or GsxAutomationPhase.SessionStart))
            {
                if (!_departureComplete)
                {
                    PublishWaitingBoard("departure sequence not started");
                }
                return;
            }

            // Order (owner-specified): reposition → GPU/chocks → jetway/stairs must all finish
            // before any departure service is called.
            if (!_groundPrep.PrepComplete)
            {
                RecordDecisionOnce("hold departure services", "waiting for ground preparation (reposition/equipment/jetway) to complete");
                PublishWaitingBoard("ground preparation running");
                return;
            }

            // Flight plan = SimBrief OFP imported into the EFB, OR a plan the PILOT loaded in
            // the MCDU (valid origin + destination ICAOs). The SimBrief importer — which
            // supplies the booked seat map and planned fuel/cargo — only runs AFTER the MCDU
            // plan is detected (the predecessor's trigger, 60 s cooldown). It must never run
            // on its own: round-5 smoke test showed the auto-import satisfying the plan gate
            // two seconds after ground prep, before the pilot had loaded anything.
            var ofpImported = _ofpImported.GetValue(false);
            var fmsOrigin = _fmsOrigin.GetValue<string?>(null);
            var fmsDestination = _fmsDestination.GetValue<string?>(null);
            var fmsPlanPresent = IsValidIcao(fmsOrigin) && IsValidIcao(fmsDestination);
            var flightPlanAvailable = ofpImported || fmsPlanPresent;
            if (fmsPlanPresent && !ofpImported && options.RequireOfpBeforeDeparture)
            {
                TryStartSimbriefImport();
            }
            if (!flightPlanAvailable && options.RequireOfpBeforeDeparture)
            {
                // Diagnostic (owner report: detection did not fire): show the raw values.
                RecordDecisionOnce(
                    "flight plan detection",
                    $"none detected — simbriefImported={ofpImported}, fmsOrigin='{fmsOrigin ?? ""}', fmsDestination='{fmsDestination ?? ""}'");
            }

            if (flightPlanAvailable)
            {
                ArmGsxPaxTarget();
            }

            // Resolve the in-flight trigger BEFORE sequencing: confirmed (mirror/cycle shows
            // GSX picked it up) → mark called, next dispatch may go out; timed out → the call
            // was silently dropped, clear it so the sequencer offers the service again.
            ResolveInFlightTrigger(options.TriggerConfirmTimeoutMs);

            var cycles = _lifecycle.SnapshotCycles();
            DepartureCycleView Cycle(string id)
            {
                cycles.TryGetValue(id, out var c);
                return new DepartureCycleView(
                    Called: c.Called,
                    ReachedRequested: c.Requested || c.Active || c.Completed,
                    ReachedActive: c.Active || c.Completed,
                    Completed: c.Completed);
            }

            var forced = _forceNext;
            var plan = DepartureSequencer.Next(
                options.DepartureServices,
                _api.Mirror.Services,
                Cycle,
                _inFlight?.ServiceId,
                flightPlanAvailable,
                options.RequireOfpBeforeDeparture,
                _isTurnaround,
                forced,
                IsCompanyHub(options),
                _ofpStore.Current?.EstimatedEnroute);
            if (forced)
            {
                _forceNext = false; // single-shot, consumed by this evaluation
                RecordDecision(
                    "force next service",
                    plan.Trigger is not null ? $"calling {plan.Trigger}" : "nothing eligible to force right now");
            }

            PublishBoard(options.DepartureServices, plan);

            foreach (var (serviceId, reason) in plan.Skipped)
            {
                RecordDecisionOnce($"skip {serviceId}", reason);
            }

            foreach (var (serviceId, reason) in plan.Holds)
            {
                RecordDecisionOnce($"hold {serviceId}", reason);
            }

            if (plan.Trigger is { } trigger && _inFlight is null)
            {
                int attempt;
                lock (_triggerAttempts)
                {
                    attempt = _triggerAttempts[trigger] = _triggerAttempts.GetValueOrDefault(trigger) + 1;
                }
                _inFlight = new InFlightTrigger(trigger, DateTimeOffset.UtcNow);
                RecordDecision(
                    $"trigger {trigger}",
                    attempt == 1 ? plan.TriggerReason ?? "next in departure order" : $"{plan.TriggerReason} (attempt {attempt})");
                _ = TriggerServiceAsync(trigger);
            }

            if (plan.AllDone)
            {
                _departureComplete = true;
                RecordDecision("departure sequence", "all departure services completed or skipped");
                _eventLog.Record("gsx-departure-complete");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Departure sequencing pump failed");
        }
        finally
        {
            _pumpLock.Release();
        }
    }

    /// <summary>Confirms or times out the one in-flight service.trigger. The command ack proves
    /// nothing — confirmation is the mirror (or a latched lifecycle edge, for quick services
    /// that bounce straight back to available) showing GSX picked the request up. Only then is
    /// the cycle marked called and the next dispatch allowed out.</summary>
    private void ResolveInFlightTrigger(int confirmTimeoutMs)
    {
        if (_inFlight is not { } inFlight)
        {
            return;
        }

        var cycles = _lifecycle.SnapshotCycles();
        cycles.TryGetValue(inFlight.ServiceId, out var cycle);
        var mirrorState = _api.Mirror.Services.TryGetValue(inFlight.ServiceId, out var info) ? info.State : (GsxServiceState?)null;
        var confirmed = cycle.Requested || cycle.Active || cycle.Completed
            || mirrorState is GsxServiceState.Requested or GsxServiceState.Active or GsxServiceState.Completed;

        if (confirmed)
        {
            _lifecycle.MarkCalled(inFlight.ServiceId);
            _inFlight = null;
            RecordDecision($"trigger {inFlight.ServiceId}", $"confirmed by GSX ({mirrorState?.ToString() ?? "lifecycle edge"})");
        }
        else if (DateTimeOffset.UtcNow - inFlight.SentAt > TimeSpan.FromMilliseconds(confirmTimeoutMs))
        {
            _inFlight = null;
            RecordDecision(
                $"trigger {inFlight.ServiceId}",
                $"not picked up by GSX within {confirmTimeoutMs / 1000} s — the call was dropped; retrying");
        }
    }

    private async Task TriggerServiceAsync(string serviceId)
    {
        var result = await _api.SendCommandAsync(
            "service.trigger",
            new JsonObject { ["service"] = serviceId }).ConfigureAwait(false);
        if (!result.Ok)
        {
            // Definitive rejection — no point waiting out the confirm window; free the
            // dispatch slot so the next pump retries (or moves on).
            if (_inFlight?.ServiceId.Equals(serviceId, StringComparison.OrdinalIgnoreCase) == true)
            {
                _inFlight = null;
            }
            RecordDecision($"trigger {serviceId}", $"rejected ({result.Code})");
            Pump();
        }
    }

    private void ArmConfiguredArrivalGate()
    {
        var gate = _options.CurrentValue.ArrivalGate;
        if (string.IsNullOrWhiteSpace(gate))
        {
            return;
        }

        if (_gateSelection.Status is GsxGateRequestStatus.Idle or GsxGateRequestStatus.Failed)
        {
            RecordDecision("arrival gate", $"arming configured gate {gate} at flight phase");
            _gateSelection.RequestGate(gate);
        }
    }

    /// <summary>Locked decision 7: handler.set autoSelectOperator once per gate session
    /// (gate context key + startup sid); feature-detected via the handlerSet capability.</summary>
    private async Task ArmAutoSelectOperatorAsync()
    {
        try
        {
            if (!_options.CurrentValue.AutoSelectOperator
                || _api.Readiness != GsxReadiness.Ready
                || !_api.HasCapability("handlerSet"))
            {
                return;
            }

            var gateKey = _api.Mirror.GateContextKey;
            if (gateKey is null)
            {
                return;
            }

            var sessionKey = $"{gateKey}|{_api.Mirror.StartupSid}";
            if (string.Equals(sessionKey, _autoSelectArmedKey, StringComparison.Ordinal))
            {
                return;
            }
            _autoSelectArmedKey = sessionKey;

            var result = await _api.SendCommandAsync("handler.set", new JsonObject
            {
                ["target"] = "gate",
                ["name"] = "autoSelectOperator",
                ["value"] = true,
            }).ConfigureAwait(false);

            RecordDecision(
                "autoSelectOperator",
                result.Ok ? $"armed for gate session {sessionKey}" : $"write failed ({result.Code}); operator menu handler remains the fallback");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "autoSelectOperator arming failed");
        }
    }

    /// <summary>Arms GSX's pax counter (L:FSDT_GSX_NUMPASSENGERS) with the booked count once
    /// per departure — GSX then boards OUR manifest (incl. any no-show randomization) instead
    /// of its own SimBrief figure (predecessor: SetPaxTarget before services). Optionally also
    /// writes the crew/pilot skip flags so GSX never asks the crew question.</summary>
    private void ArmGsxPaxTarget()
    {
        if (_paxTargetArmed)
        {
            return;
        }

        var booked = ProsimCompanion.Core.Aircraft.SeatMap
            .Parse(_bookedSeatString.GetValue<string?>(null))
            .Count(seat => seat);
        if (booked <= 0)
        {
            return; // booked map not written yet — retry on a later pump
        }

        _paxTargetArmed = true;
        _ = ArmGsxPaxTargetAsync(booked);
    }

    private async Task ArmGsxPaxTargetAsync(int booked)
    {
        try
        {
            await _simVars.WriteAsync(GsxLvarNames.NumPassengers, booked).ConfigureAwait(false);
            RecordDecision("pax target", $"armed GSX with {booked} passengers (booked manifest)");

            if (_options.CurrentValue.SkipCrewBoardingQuestion)
            {
                await _simVars.WriteAsync(GsxLvarNames.CrewNotBoarding, 1).ConfigureAwait(false);
                await _simVars.WriteAsync(GsxLvarNames.PilotsNotBoarding, 1).ConfigureAwait(false);
                await _simVars.WriteAsync(GsxLvarNames.CrewNotDeboarding, 1).ConfigureAwait(false);
                await _simVars.WriteAsync(GsxLvarNames.PilotsNotDeboarding, 1).ConfigureAwait(false);
                RecordDecision("pax target", "crew/pilot boarding questions suppressed via LVARs");
            }
        }
        catch (InvalidOperationException ex)
        {
            _paxTargetArmed = false; // MSFS not connected yet — retry on a later pump
            _logger.LogDebug("Pax target arming deferred: {Reason}", ex.Message);
        }
    }

    /// <summary>The MCDU FMS origin/destination datarefs carry a valid 4-char ICAO once the
    /// pilot loads a plan — but read "----" before that, and have been observed returning the
    /// literal string "Null" (predecessor archaeology, TakeoffPerfService).</summary>
    private static bool IsValidIcao(string? value)
        => value is { Length: 4 }
            && value != "----"
            && !value.Equals("Null", StringComparison.OrdinalIgnoreCase);

    /// <summary>Fires the SimBrief import in the background with a 60 s cooldown; the importer
    /// itself decision-logs its progress, and a success re-pumps the sequencer. Only invoked
    /// once the pilot's MCDU plan is detected — never preemptively.</summary>
    private void TryStartSimbriefImport()
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastImportAttempt < TimeSpan.FromSeconds(60))
        {
            return;
        }
        _lastImportAttempt = now;

        _ = Task.Run(async () =>
        {
            try
            {
                var outcome = await _simbrief.TryImportAsync().ConfigureAwait(false);
                if (outcome == SimbriefImportOutcome.Imported)
                {
                    Pump();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SimBrief import attempt failed");
            }
        });
    }

    /// <summary>Publishes the Prosim2GSX-style departure status board from the current plan,
    /// mirror states and lifecycle cycles, in configured step order. "Called" is truthful now:
    /// it means a trigger is in flight or GSX confirmed the call — never a dropped request.</summary>
    private void PublishBoard(IReadOnlyList<DepartureServiceStep> steps, DeparturePlan plan)
    {
        var holds = plan.Holds.ToDictionary(h => h.ServiceId, h => h.Reason, StringComparer.OrdinalIgnoreCase);
        var skips = plan.Skipped.ToDictionary(s => s.ServiceId, s => s.Reason, StringComparer.OrdinalIgnoreCase);
        var services = _api.Mirror.Services;
        var inFlight = _inFlight?.ServiceId;

        var rows = new List<GsxServiceBoardRow>();
        foreach (var id in DistinctServiceIds(steps))
        {
            services.TryGetValue(id, out var info);
            var called = _lifecycle.IsPending(id)
                || string.Equals(id, plan.Trigger, StringComparison.OrdinalIgnoreCase)
                || string.Equals(id, inFlight, StringComparison.OrdinalIgnoreCase);
            var row = _lifecycle.IsCompleted(id) ? new GsxServiceBoardRow(id, GsxServiceStage.Completed, null)
                : info?.State == GsxServiceState.Active ? new GsxServiceBoardRow(id, GsxServiceStage.Active, info.ProgressText)
                : info?.State == GsxServiceState.Requested ? new GsxServiceBoardRow(id, GsxServiceStage.Requested, null)
                : called ? new GsxServiceBoardRow(id, GsxServiceStage.Called, holds.GetValueOrDefault(id))
                : skips.TryGetValue(id, out var skipReason) ? new GsxServiceBoardRow(id, GsxServiceStage.Skipped, skipReason)
                : holds.TryGetValue(id, out var holdReason) ? new GsxServiceBoardRow(id, GsxServiceStage.Held, holdReason)
                : new GsxServiceBoardRow(id, GsxServiceStage.Waiting, null);
            rows.Add(row);
        }

        _diagnostics.UpdateServiceBoard(rows);
    }

    /// <summary>Board shown before sequencing runs: every non-completed service Waiting with
    /// one shared reason (sequence not started / ground prep).</summary>
    private void PublishWaitingBoard(string reason)
    {
        var rows = DistinctServiceIds(_options.CurrentValue.DepartureServices)
            .Select(id => _lifecycle.IsCompleted(id)
                ? new GsxServiceBoardRow(id, GsxServiceStage.Completed, null)
                : new GsxServiceBoardRow(id, GsxServiceStage.Waiting, reason))
            .ToList();
        _diagnostics.UpdateServiceBoard(rows);
    }

    private static IEnumerable<string> DistinctServiceIds(IReadOnlyList<DepartureServiceStep> steps)
        => steps
            .Select(step => step.Service)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether the currently loaded airport matches a configured company-hub ICAO
    /// prefix (case-insensitive). Unknown airport ⇒ non-hub, so hub-only services simply hold
    /// off rather than firing at an unidentified field.</summary>
    private bool IsCompanyHub(GsxOptions options)
    {
        var icao = _api.Mirror.AirportIcao;
        if (string.IsNullOrWhiteSpace(icao) || options.CompanyHubs.Count == 0)
        {
            return false;
        }
        return options.CompanyHubs.Any(prefix =>
            !string.IsNullOrWhiteSpace(prefix)
            && icao.StartsWith(prefix.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private void RecordDecision(string action, string reason)
    {
        lock (_lastReasonByAction)
        {
            _lastReasonByAction[action] = reason;
        }
        _logger.LogInformation("GSX automation: {Action} — {Reason}", action, reason);
        _diagnostics.RecordDecision(new GsxDecisionView(DateTimeOffset.UtcNow, action, reason));
        _eventLog.Record("gsx-decision", new { action, reason });
    }

    /// <summary>Deduplicated per action: records only when that action's reason changes, so a
    /// stable hold never floods the log (smoke-test find: a single shared last-summary let
    /// alternating actions defeat the dedupe).</summary>
    private void RecordDecisionOnce(string action, string reason)
    {
        lock (_lastReasonByAction)
        {
            if (_lastReasonByAction.TryGetValue(action, out var last)
                && string.Equals(last, reason, StringComparison.Ordinal))
            {
                return;
            }
        }
        RecordDecision(action, reason);
    }
}
