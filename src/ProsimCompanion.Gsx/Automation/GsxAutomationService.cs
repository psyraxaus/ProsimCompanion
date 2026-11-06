using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Gsx.Gate;
using ProsimCompanion.Gsx.Services;
using ProsimCompanion.Gsx.Sync;

namespace ProsimCompanion.Gsx.Automation;

/// <summary>
/// The ground-automation host: tracks the automation phase from the central flight state
/// engine, runs the departure service sequence (OFP-gated, cursor + per-service activation
/// rules — see <see cref="DepartureSequencer"/>), arms the configured arrival gate when
/// reaching flight, writes handler.set autoSelectOperator once per gate session, and resets
/// service cycles on arrival. All trigger dispatch goes through the shared
/// <see cref="IGsxTriggerSlot"/> — this module is a slot client like every other sender; the
/// sequencer's retry-forever behaviour emerges from re-offering a dropped service on the next
/// evaluation, not from a watcher policy. Every decision — including holds and skips — is
/// recorded with its reason (decision log + session event log), deduplicated so a stable
/// state does not spam.
/// <para>
/// The pump itself is three modules behind two seams (architecture review C1): the inputs
/// gatherer (<see cref="DeparturePumpGatherer"/>) reads the collaborators into the pure
/// core's inputs, the effect planner (<see cref="DeparturePumpEffectPlanner"/>) turns the
/// core's outcome into an ordered effect list, and the applier
/// (<see cref="DeparturePumpEffectApplier"/>) performs each effect. What stays here is the
/// timer/event wiring, the pump lock (<see cref="LockedPumpRunner"/>), the latch storage
/// (<see cref="DepartureCycleLatches"/>) and the public surface the web, commands and DI call.
/// </para>
/// </summary>
public sealed class GsxAutomationService : IDisposable, IGsxDepartureControl, IGsxDepartureStarter, IGsxAutomationPhaseView
{
    private static readonly TimeSpan PumpInterval = TimeSpan.FromSeconds(3);

    private readonly IGsxRemoteApi _api;
    private readonly IGsxTriggerSlot _slot;
    private readonly GsxServiceLifecycleTracker _lifecycle;
    private readonly IGsxArrivalGateRequester _gateSelection;
    private readonly DepartureCycleState _cycle;
    private readonly IFlightPhaseSource _flightState;
    private readonly IOptionsMonitor<GsxOptions> _options;
    private readonly IGsxDecisionLog _decisions;
    private readonly ILogger<GsxAutomationService> _logger;
    private readonly IGsxFlightPlanStatus _flightPlan;
    private readonly DeparturePumpGatherer _gatherer;
    private readonly DeparturePumpEffectApplier _applier;
    private readonly DepartureCycleLatches _latches;
    private readonly GsxPaxTargetArmer _paxTarget;
    private readonly LockedPumpRunner _runner = new();

    /// <summary>The "next service" smart-button subscriptions (configurable since 2026-10-17,
    /// <see cref="GsxOptions.SmartButtonDataRef"/>): both INT/RAD rockers by default, else the
    /// one switch the pilot named. Swapped under <see cref="_smartButtonLock"/> when the
    /// dataref setting changes, so a live edit needs no restart.</summary>
    private readonly List<IDataRefSubscription<int>> _smartButton = new();
    private readonly object _smartButtonLock = new();
    private readonly IProsimDataRefs _prosim;
    private readonly IDisposable? _optionsChange;
    private string _smartButtonDataRef = "";

    /// <summary>The configured dataref pushed something that is not a number: warned once,
    /// the trigger stays off until the setting changes (never an InvalidCastException —
    /// the typed path serves <see cref="SmartButtonRule.NoValue"/> instead).</summary>
    private volatile bool _smartButtonUnreadable;
    private readonly Timer _pumpTimer;
    private readonly IDisposable _fuelConfirmationObserver;
    private string? _autoSelectArmedId;

    private readonly GroundOpsSignals _groundOpsSignals;
    private readonly OfpStore _ofpStore;
    private readonly ILegBoundaryEvents _legBoundary;
    private readonly CompanionTrackingLvars _tracking;
    private string? _lastResetPlanId;
    private readonly GsxResyncState _resyncState;
    private readonly FuelConfirmationStore _fuelConfirmation;
    private readonly DeiceRequestStore _deiceRequest;
    private readonly IDisposable _deiceObserver;
    private readonly TurnaroundStartStore _turnaroundStart;
    private readonly Core.Day.DutyDaySignals? _dutyDay;

    public GsxAutomationService(
        IGsxRemoteApi api,
        IGsxTriggerSlot slot,
        GsxServiceLifecycleTracker lifecycle,
        IGsxArrivalGateRequester gateSelection,
        DepartureCycleState cycle,
        IFlightPhaseSource flightState,
        IProsimDataRefs prosim,
        IGsxFlightPlanStatus flightPlan,
        OfpStore ofpStore,
        IOptionsMonitor<GsxOptions> options,
        GroundOpsSignals groundOpsSignals,
        GsxResyncState resyncState,
        FuelConfirmationStore fuelConfirmation,
        DeiceRequestStore deiceRequest,
        TurnaroundStartStore turnaroundStart,
        ILegBoundaryEvents legBoundary,
        DeparturePumpGatherer gatherer,
        DeparturePumpEffectApplier applier,
        DepartureCycleLatches latches,
        GsxPaxTargetArmer paxTarget,
        IGsxDecisionLog decisions,
        CompanionTrackingLvars tracking,
        ILogger<GsxAutomationService> logger,
        Core.Day.DutyDaySignals? dutyDay = null)
    {
        ArgumentNullException.ThrowIfNull(tracking);
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(gateSelection);
        ArgumentNullException.ThrowIfNull(cycle);
        ArgumentNullException.ThrowIfNull(flightState);
        ArgumentNullException.ThrowIfNull(prosim);
        ArgumentNullException.ThrowIfNull(flightPlan);
        ArgumentNullException.ThrowIfNull(ofpStore);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(groundOpsSignals);
        ArgumentNullException.ThrowIfNull(resyncState);
        ArgumentNullException.ThrowIfNull(fuelConfirmation);
        ArgumentNullException.ThrowIfNull(deiceRequest);
        ArgumentNullException.ThrowIfNull(turnaroundStart);
        ArgumentNullException.ThrowIfNull(legBoundary);
        ArgumentNullException.ThrowIfNull(gatherer);
        ArgumentNullException.ThrowIfNull(applier);
        ArgumentNullException.ThrowIfNull(latches);
        ArgumentNullException.ThrowIfNull(paxTarget);
        ArgumentNullException.ThrowIfNull(decisions);
        ArgumentNullException.ThrowIfNull(logger);

        _api = api;
        _slot = slot;
        _lifecycle = lifecycle;
        _gateSelection = gateSelection;
        _cycle = cycle;
        _flightState = flightState;
        _prosim = prosim;
        _flightPlan = flightPlan;
        _ofpStore = ofpStore;
        _legBoundary = legBoundary;
        _options = options;
        _groundOpsSignals = groundOpsSignals;
        _resyncState = resyncState;
        _fuelConfirmation = fuelConfirmation;
        _deiceRequest = deiceRequest;
        _turnaroundStart = turnaroundStart;
        _gatherer = gatherer;
        _applier = applier;
        _latches = latches;
        _paxTarget = paxTarget;
        _decisions = decisions;
        _logger = logger;
        _dutyDay = dutyDay;
        _tracking = tracking;

        // The cockpit "smart button" (predecessor semantics): a switch reaching its trigger
        // value force-calls the next departure service. Default: INT/RAD to INT (0) on either
        // ACP; a pilot whose audio bridge keys the interphone mic on INT names another switch
        // or turns it off (gsx.smartButton*, 2026-10-17) — live, through the options monitor.
        SubscribeSmartButton(options.CurrentValue.SmartButtonDataRef);
        _optionsChange = options.OnChange(o =>
        {
            if (!string.Equals((o.SmartButtonDataRef ?? "").Trim(), _smartButtonDataRef, StringComparison.Ordinal))
            {
                SubscribeSmartButton(o.SmartButtonDataRef);
            }
        });

        _flightState.PhaseChanged += OnFlightPhaseChanged;
        _lifecycle.ServiceEvent += OnServiceEvent;
        _api.Mirror.Updated += OnMirrorUpdated;
        _resyncState.Assessed += OnResyncAssessed;
        // Slot occupancy changes (confirm, drop, rejection) re-evaluate the sequence promptly
        // instead of waiting out the 3 s pump interval; cycle changes (notably the prep
        // coordinator marking PrepComplete) do the same.
        _slot.Changed += Pump;
        _cycle.Changed += Pump;
        // A fuel confirmation releases the Refueling hold at once, not on the next 3 s pump.
        _fuelConfirmationObserver = _fuelConfirmation.Observe(_ => Pump());
        // The de-icing verdict / the captain's answer reshapes the step list at once.
        _deiceObserver = _deiceRequest.Observe(_ => Pump());
        // The captain's answer to "commence ground services?" (issue #169, ask-first).
        _turnaroundStart.Answered += OnTurnaroundStartAnswered;
        // An automation-started SimBrief import that landed satisfies the plan gate now.
        _applier.SimbriefImported += Pump;
        // A new plan is a new leg (owner rule 2026-11-01): the departure side starts over. The
        // leg-boundary machine is the ONE judge of "a different plan" (ADR-0019 §1; review
        // 2026-11-05 — this service used to decide it again from the OFP store with its own
        // compare); what stays here is the GSX-side gate: parked, and boarding not started.
        _legBoundary.LegBegan += OnLegBegan;
        if (_dutyDay is not null)
        {
            _dutyDay.DayEnded += OnDutyDayEnded;
        }

        _pumpTimer = new Timer(_ => Pump(), null, PumpInterval, PumpInterval);
    }

    /// <summary>End of duty (ADR-0019 §8, issue #218): the departure side starts over and
    /// ground preparation waits for a NEW plan — the plan in the store becomes the turnaround
    /// baseline, so neither the auto-start nor a same-plan re-import revives the old leg; only
    /// a different OFP (or the pilot's own "commence ground services") starts anything. The
    /// arrival side (deboarding, GPU, jetway) is left as it stands — the aircraft is where it is.</summary>
    private void OnDutyDayEnded(Core.Day.DutyDayEnded ended)
    {
        try
        {
            var current = _ofpStore.Current;
            var trackingLvars = ResetDepartureCycle("end of duty — departure cycle cleared");
            _latches.ResetForArrival(current);
            _lastResetPlanId = NewPlanCycleReset.PlanId(current) ?? _lastResetPlanId;
            _turnaroundStart.Withdraw();
            _decisions.Record("departure cycle reset", $"end of duty ({ended.Source}) — the departure services wait for a new flight plan");
            _decisions.RecordEvent("gsx-cycle-reset", new
            {
                reason = "end of duty",
                planId = NewPlanCycleReset.PlanId(current),
                origin = current?.OriginIcao,
                destination = current?.DestinationIcao,
                source = ended.Source,
                trackingLvars,
            });
            Pump();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "End-of-duty departure cycle reset failed");
        }
    }

    /// <summary>"A new plan is a new leg" (EHAM 2026-11-01: the app restarted at the gate after
    /// the arrival, the resync took the arrival leg's boarding for this leg's, the sequence
    /// closed with nothing called, and leg two's plan landed into a finished cycle). A different
    /// SimBrief plan while parked with the engines off resets the departure side — service
    /// cycles, pax target, trigger slot, tracking memory — unless a departure service for the
    /// plan the cycle was opened with is already under way. Rule: <see cref="NewPlanCycleReset"/>.</summary>
    private void OnLegBegan(object? sender, LegBeganEventArgs e)
    {
        try
        {
            var current = _ofpStore.Current;
            var data = _flightState.Snapshot().Data;
            // Only a boarding under way holds the old leg (ADR-0019 §6, issue #213): refuel
            // and catering already run for the old plan are reset like the rest and re-called.
            var boardingObserved = _lifecycle.ObservedProgress(GsxServiceIds.Boarding);
            var cycleWasComplete = _cycle.Complete; // read BEFORE the reset clears it (#223)
            if (!NewPlanCycleReset.ShouldReset(current, _lastResetPlanId, boardingObserved, data))
            {
                return;
            }

            var id = NewPlanCycleReset.PlanId(current)!;
            _lastResetPlanId = id;
            var trackingLvars = ResetDepartureCycle("new flight plan — departure cycle reset");
            _decisions.Record("departure cycle reset", $"new flight plan {id} ({current?.OriginIcao}→{current?.DestinationIcao}) — the departure services start over for the new leg");
            _decisions.RecordEvent("gsx-cycle-reset", new
            {
                reason = "new flight plan",
                planId = id,
                origin = current?.OriginIcao,
                destination = current?.DestinationIcao,
                cycleWasComplete,
                flightCycleReset = true,
                trackingLvars,
            });
            // Everything the arrival resets, the new plan resets too (ADR-0019 §6): the gate
            // monitor, the loadsheet editions, the upcall and notification latches, the
            // walkaround — EHAM 2026-11-01: leg two's "ready for pushback" was never sent
            // because leg one's latch still stood. Kind NewPlan: the startup resync's
            // turnaround LVARs and the restart snapshot leave this one alone.
            _groundOpsSignals.RaiseFlightCycleReset(FlightCycleResetKind.NewPlan);
            Pump();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "New-plan departure cycle reset failed");
        }
    }

    /// <summary>The six-step departure reset both the end of duty and the new plan run (review
    /// 2026-11-05: each handler re-typed it): the departure-side service cycles, a fresh
    /// turnaround cycle, the pax target, the trigger slot and the tracking LVARs. The arrival
    /// side (deboarding, GPU, jetway) is left as it stands. Returns what the cycle-reset event
    /// says about the tracking clear.</summary>
    private string ResetDepartureCycle(string slotReason)
    {
        _lifecycle.ResetCycle(NewPlanCycleReset.ArrivalSide);
        _cycle.BeginTurnaroundCycle();
        _paxTarget.Reset();
        _slot.Reset(slotReason);
        return ClearDepartureTracking();
    }

    /// <summary>Clears the companion tracking LVARs of the departure one-shots and the
    /// loadsheet editions, so a restart after the reset resyncs to a fresh leg too. The arrival
    /// side's (deboarding, GPU) stay. Through the one writer (issue #230): with MSFS down the
    /// clear waits in its queue for the SimConnect connected edge instead of being lost — the
    /// direct writes this replaced cleared nothing, and the next restart resynced to the OLD
    /// leg's progress. Returns what the cycle-reset event says about it.</summary>
    private string ClearDepartureTracking()
    {
        _ = _tracking.ClearDepartureSideAsync();
        return _tracking.IsSimConnected ? "cleared" : "queued until MSFS connects";
    }

    /// <summary>The startup resync finished: adopt the recovered turnaround flag (the
    /// in-memory one only rises on an in-session arrival) and let the held sequencer run.</summary>
    private void OnResyncAssessed()
    {
        if (_resyncState.TurnaroundDetected && !_cycle.IsTurnaround)
        {
            _cycle.MarkTurnaround();
            // The OFP store is per process: anything it holds (or receives) after this
            // restart is the next leg's plan as far as the turnaround auto-start is concerned.
            _latches.SetArrivalBaseline(null);
            _decisions.Record("turnaround", "recovered from tracking LVARs by the startup resync");
        }

        RestoreCompletedCycleFromSeeds();
        Pump();
    }

    /// <summary>Every configured departure service seeded complete at the gate = the cycle is
    /// over (EHAM 2026-11-01: restart after boarding, the app sat in PREFLIGHT with the beacon
    /// on because the cycle was never marked started, let alone complete). Marks the cycle,
    /// pins the plan it belongs to so a same-plan re-import does not reset it, and raises
    /// "departure services completed" (ready for pushback). Rule: <see cref="ResyncCycleRestore"/>.</summary>
    private void RestoreCompletedCycleFromSeeds()
    {
        if (_cycle.Started || Phase is not (GsxAutomationPhase.Preparation or GsxAutomationPhase.SessionStart))
        {
            return;
        }

        var steps = _options.CurrentValue.DepartureServices;
        if (!ResyncCycleRestore.AllConfiguredCompleted(steps, _lifecycle.IsCompleted))
        {
            return;
        }

        _cycle.MarkStarted();
        _cycle.MarkComplete();
        _lastResetPlanId = NewPlanCycleReset.PlanId(_ofpStore.Current) ?? _lastResetPlanId;
        _decisions.Record("departure sequence", "every configured departure service was already completed before the restart — cycle restored complete (ready for pushback)");
        _decisions.RecordEvent("gsx-departure-complete", new { reason = "restored by the startup resync", services = steps.Select(s => s.Service).ToArray() });
        _groundOpsSignals.RaiseDepartureServicesCompleted();
    }

    /// <summary>Ask-first turnaround (issue #169): yes starts the sequence as a voice start;
    /// no (or no answer) leaves the voice gate exactly as it was.</summary>
    private void OnTurnaroundStartAnswered(object? sender, (bool Accepted, string Source) answer)
    {
        if (answer.Accepted)
        {
            if (!_cycle.Started)
            {
                StartDepartureServices(answer.Source);
            }

            return;
        }

        _latches.MarkTurnaroundDeclined();
        _decisions.Record(
            "departure sequence",
            $"turnaround auto-start declined ({answer.Source}) — waiting for 'commence ground services' or the Start button");
        Pump();
    }

    /// <summary>Current automation phase (derived, never re-computed by features).</summary>
    public GsxAutomationPhase Phase { get; private set; } = GsxAutomationPhase.SessionStart;

    /// <summary>True once the departure sequence has been started (manually or automatically).</summary>
    public bool DepartureStarted => _cycle.Started;

    /// <summary>True once every departure service completed or was skipped — the
    /// beacon-orchestrated pushback sequence arms on this.</summary>
    public bool DepartureComplete => _cycle.Complete;

    bool IGsxDepartureControl.Started => _cycle.Started;

    bool IGsxDepartureControl.Complete => _cycle.Complete;

    void IGsxDepartureControl.Start(string source) => StartDepartureServices(source);

    void IGsxDepartureControl.ForceNext() => ForceNextService("web");

    /// <summary>Starts the departure service sequence (idempotent). The source — web, voice,
    /// hail, command, api, streamdeck, auto, auto-turnaround — goes on the decision and the
    /// <c>gsx-departure-started</c> session event (issue #169).</summary>
    public void StartDepartureServices(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (!_applier.StartCycle(source))
        {
            return;
        }

        Pump();
    }

    /// <summary>Single-shot force-next (INT/RAD / web button): the next evaluation bypasses the
    /// current step's activation rule — including Manual. Consumed by that evaluation whether or
    /// not anything could be called; the flight-plan gate is never bypassed.</summary>
    public void ForceNextService(string source)
    {
        if (!_cycle.Started || _cycle.Complete)
        {
            _decisions.Record("force next service", $"{source}: ignored — departure sequence not running");
            return;
        }

        _latches.SetForceNext(true);
        _decisions.Record("force next service", $"requested by {source}");
        Pump();
    }

    public void Dispose()
    {
        _slot.Changed -= Pump;
        _cycle.Changed -= Pump;
        _flightState.PhaseChanged -= OnFlightPhaseChanged;
        _lifecycle.ServiceEvent -= OnServiceEvent;
        _api.Mirror.Updated -= OnMirrorUpdated;
        _resyncState.Assessed -= OnResyncAssessed;
        _optionsChange?.Dispose();
        _fuelConfirmationObserver.Dispose();
        _deiceObserver.Dispose();
        _turnaroundStart.Answered -= OnTurnaroundStartAnswered;
        _applier.SimbriefImported -= Pump;
        _legBoundary.LegBegan -= OnLegBegan;
        if (_dutyDay is not null)
        {
            _dutyDay.DayEnded -= OnDutyDayEnded;
        }

        _pumpTimer.Dispose();
        DisposeSmartButton();
        _runner.Dispose();
    }

    /// <summary>(Re)builds the smart-button subscriptions for <paramref name="dataRef"/>:
    /// the two INT/RAD rockers when empty, else the named switch read as a number through the
    /// typed path (<see cref="SmartButtonRule.NoValue"/> until it reports one). The old
    /// handles are released after the new ones register, so a shared registration is never
    /// dropped and re-created. A name the table refuses logs once and leaves the trigger off.</summary>
    private void SubscribeSmartButton(string? dataRef)
    {
        var name = (dataRef ?? "").Trim();
        List<IDataRefSubscription<int>> previous;
        lock (_smartButtonLock)
        {
            previous = new List<IDataRefSubscription<int>>(_smartButton);
            _smartButton.Clear();
            _smartButtonDataRef = name;
            _smartButtonUnreadable = false;
            try
            {
                if (SmartButtonRule.UsesDefaultRocker(name))
                {
                    _smartButton.Add(_prosim.Subscribe(ProsimDataRefNames.IntRadCpt));
                    _smartButton.Add(_prosim.Subscribe(ProsimDataRefNames.IntRadFo));
                }
                else
                {
                    _smartButton.Add(_prosim.Subscribe(new DataRef<int>(name, DataRefTier.Frequent, SmartButtonRule.NoValue)));
                }

                foreach (var subscription in _smartButton)
                {
                    subscription.ValueChanged += OnSmartButtonChanged;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidCastException)
            {
                _logger.LogWarning(ex, "Smart button dataref {DataRef} could not be subscribed — hardware next-service trigger off until the setting changes", name);
                ReleaseSmartButton(_smartButton);
            }
        }

        ReleaseSmartButton(previous);
        if (previous.Count > 0)
        {
            _logger.LogInformation("Smart button now follows {Trigger}", name.Length == 0 ? "the INT/RAD rockers" : name);
        }
    }

    private void ReleaseSmartButton(List<IDataRefSubscription<int>> subscriptions)
    {
        foreach (var subscription in subscriptions)
        {
            subscription.ValueChanged -= OnSmartButtonChanged;
            subscription.Dispose();
        }

        subscriptions.Clear();
    }

    private void DisposeSmartButton()
    {
        lock (_smartButtonLock)
        {
            ReleaseSmartButton(_smartButton);
        }
    }

    /// <summary>Fires on every movement of the smart-button switch; the configured trigger
    /// value (0 = INT on the default rocker) calls the next service. Only consumed while the
    /// departure sequence is running on the ground — the switch is a real cockpit control in
    /// every other phase (<see cref="SmartButtonRule"/>).</summary>
    private void OnSmartButtonChanged(object? sender, EventArgs e)
    {
        if (_smartButtonUnreadable)
        {
            return;
        }

        var subscription = (IDataRefSubscription<int>)sender!;
        var value = subscription.Value;
        if (value == SmartButtonRule.NoValue && subscription.RawValue is not null)
        {
            _smartButtonUnreadable = true;
            _logger.LogWarning("Smart button dataref {DataRef} reports {Value}, which is not a number — hardware next-service trigger off until the setting changes", subscription.Name, subscription.RawValue);
            return;
        }

        var options = _options.CurrentValue;
        if (SmartButtonRule.ShouldFire(options, value, Phase, _cycle.Started, _cycle.Complete))
        {
            ForceNextService(SmartButtonRule.Source(options.SmartButtonDataRef));
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
        _decisions.Record("automation phase", phase.ToString());
        _decisions.RecordEvent("gsx-automation-phase", new { phase = phase.ToString() });

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
                _cycle.BeginTurnaroundCycle();
                _paxTarget.Reset();
                // Turnaround auto-start bookkeeping (issue #169): the plan we flew in on is
                // the baseline — the next leg's OFP is whatever replaces it.
                _latches.ResetForArrival(_ofpStore.Current);
                _slot.Reset("service cycles reset after arrival");
                // Cross-feature cycle boundary: loadsheet caches/edition counters reset here.
                _groundOpsSignals.RaiseFlightCycleReset();
                _decisions.Record("turnaround", "service cycles reset after arrival");
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

    private void OnMirrorUpdated(string mirrorEntry)
    {
        if (string.Equals(mirrorEntry, "handlerData", StringComparison.OrdinalIgnoreCase))
        {
            _ = ArmAutoSelectOperatorAsync();
        }
        else if (string.Equals(mirrorEntry, "services", StringComparison.OrdinalIgnoreCase))
        {
            Pump();
        }
    }

    /// <summary>One sequencing evaluation. Serialized and re-entrant-safe; cheap when idle:
    /// gather → evaluate (pure core) → plan (pure) → apply, under the pump lock, with the
    /// after-lock effects (issue #166) applied once the lock is released.</summary>
    private void Pump()
        => _runner.TryRun(PlanPump, _applier, ex => _logger.LogError(ex, "Departure sequencing pump failed"));

    private IReadOnlyList<DeparturePumpEffect> PlanPump()
    {
        var now = DateTimeOffset.UtcNow;
        var latches = _latches.Snapshot();
        var reading = _gatherer.Read(Phase, latches, now);
        // All policy — gates, ordering, sequencing — is the pure core (campaign #78);
        // the effect order is the planner's (architecture review C1).
        var outcome = DepartureAutomationCore.Evaluate(reading.Inputs);
        return DeparturePumpEffectPlanner.Plan(reading, outcome, latches);
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
            _decisions.Record("arrival gate", $"arming configured gate {gate} at flight phase");
            _gateSelection.RequestGate(gate);
        }
    }

    /// <summary>Locked decision 7: handler.set autoSelectOperator once per gate session
    /// (gate context + startup sid); feature-detected via the handlerSet capability.</summary>
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

            var gateContext = _api.Mirror.GateContextKey;
            if (gateContext is null)
            {
                return;
            }

            var sessionId = $"{gateContext}|{_api.Mirror.StartupSid}";
            if (string.Equals(sessionId, _autoSelectArmedId, StringComparison.Ordinal))
            {
                return;
            }
            _autoSelectArmedId = sessionId;

            var result = await _api.SendCommandAsync("handler.set", new JsonObject
            {
                ["target"] = "gate",
                ["name"] = "autoSelectOperator",
                ["value"] = true,
            }).ConfigureAwait(false);

            _decisions.Record(
                "autoSelectOperator",
                result.Ok ? $"armed for gate session {sessionId}" : $"write failed ({result.Code}); operator menu handler remains the fallback");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "autoSelectOperator arming failed");
        }
    }

    bool IGsxDepartureStarter.Started => _cycle.Started;

    /// <summary>Hail/command path into the cycle (issue #169): the pax target is armed from
    /// the current plan BEFORE the cycle is marked started, so a Boarding call never boards
    /// GSX's stale count; with no plan at all the call is refused. The sequencer then takes
    /// over the remaining steps exactly as after "commence ground services".</summary>
    public async Task<DepartureStartForCallOutcome> StartForServiceCallAsync(
        string source, bool requirePaxTarget, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        cancellationToken.ThrowIfCancellationRequested();
        if (_cycle.Started)
        {
            return DepartureStartForCallOutcome.Ok;
        }

        if (_ofpStore.Current is null && !_flightPlan.FlightPlanAvailable)
        {
            return DepartureStartForCallOutcome.Refused("No flight plan loaded yet — load the OFP first.");
        }

        var armed = await _paxTarget.TryArmAsync().ConfigureAwait(false);
        if (requirePaxTarget && !armed)
        {
            return DepartureStartForCallOutcome.Refused(
                _paxTarget.BookedPax() <= 0
                    ? "No flight plan loaded yet — load the OFP first (the passenger manifest is empty)."
                    : "The passenger count could not be sent to GSX yet (MSFS not connected) — try again in a moment.");
        }

        if (_applier.StartCycle(source))
        {
            Pump();
        }

        return DepartureStartForCallOutcome.Ok;
    }
}
