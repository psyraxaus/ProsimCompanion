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
/// service cycles on arrival. All trigger dispatch goes through the shared
/// <see cref="IGsxTriggerSlot"/> — this module is a slot client like every other sender; the
/// sequencer's retry-forever behaviour emerges from re-offering a dropped service on the next
/// evaluation, not from a watcher policy. Every decision — including holds and skips — is
/// recorded with its reason (decision log + session event log), deduplicated so a stable
/// state does not spam.
/// </summary>
public sealed class GsxAutomationService : IDisposable, IGsxDepartureControl, IGsxDepartureStarter
{
    private static readonly TimeSpan PumpInterval = TimeSpan.FromSeconds(3);

    private readonly IGsxRemoteApi _api;
    private readonly IGsxTriggerSlot _slot;
    private readonly GsxServiceLifecycleTracker _lifecycle;
    private readonly GsxGateSelectionService _gateSelection;
    private readonly DepartureCycleState _cycle;
    private readonly IFlightPhaseSource _flightState;
    private readonly IOptionsMonitor<GsxOptions> _options;
    private readonly GsxDiagnosticsStore _diagnostics;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<GsxAutomationService> _logger;
    private readonly ISimVars _simVars;
    private readonly IGsxFlightPlanStatus _flightPlan;
    private readonly IDataRefSubscription<string?> _bookedSeatString;

    // Tankering pre-skip inputs (#117): cached reads only, same refs the refuel sync latches.
    private readonly IDataRefSubscription<double> _fuelTotal;
    private readonly IDataRefSubscription<double> _plannedFuel;

    /// <summary>The refuel-active edge was raised in place of a tankering-skipped refuel this
    /// cycle (#117) — once per turnaround, reset at arrival.</summary>
    private bool _tankeringPrelimRaised;

    /// <summary>EFB planned-fuel settle window (#118, 2026-09-05): with no OFP yet, the
    /// tankering decision trusts <c>efb.plannedfuel</c> only after the plan has stood this
    /// long — the import the plan detection itself starts needs time to overwrite the
    /// previous leg's figure (imports land in seconds; failures leave the pilot's own EFB
    /// entry as the honest fallback).</summary>
    private static readonly TimeSpan TankeringPlanSettle = TimeSpan.FromSeconds(90);

    /// <summary>Hold reason for the Refueling step under <c>gsx.refuelCall = onFuelConfirmed</c>
    /// (2026-09-19). Probed by <c>refuel-on-fuel-confirmed-hold</c> — keep the wording stable.</summary>
    internal const string FuelConfirmationHoldReason =
        "waiting for the crew to confirm the block fuel (INIT page CONFIRM FUEL, "
        + "'fuel confirmed' by voice, or 'request refueling') — other services continue";

    /// <summary>UTC instant the flight plan first read as available this cycle; null while
    /// none stands. Reset at arrival with the rest of the cycle state.</summary>
    private DateTimeOffset? _planAvailableSinceUtc;
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
    private readonly SemaphoreSlim _pumpLock = new(1, 1);
    private readonly Dictionary<string, string> _lastReasonByAction = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _triggerAttempts = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _forceNext;
    private bool _paxTargetArmed;
    private string? _autoSelectArmedKey;
    private DateTimeOffset _lastImportAttempt = DateTimeOffset.MinValue;

    private readonly ISimbriefImporter _simbrief;
    private readonly GroundOpsSignals _groundOpsSignals;
    private readonly OfpStore _ofpStore;
    private readonly GsxResyncState _resyncState;
    private readonly FuelConfirmationStore _fuelConfirmation;
    private readonly IEfbInitOverrides _initOverrides;
    private readonly DeiceRequestStore _deiceRequest;
    private readonly IDisposable _deiceObserver;
    private readonly TurnaroundStartStore _turnaroundStart;

    /// <summary>The plan that was current when the aircraft arrived (issue #169): the next
    /// leg's OFP is "loaded" once the store holds a different one. Null with
    /// <see cref="_arrivalOfpKnown"/> true after a resync-recovered turnaround — the store is
    /// in-memory, so whatever it holds was imported after the restart and counts as new.</summary>
    private OfpData? _arrivalOfp;
    private bool _arrivalOfpKnown;

    /// <summary>voiceThenAuto settle stamp: when the turnaround auto-start conditions first
    /// held (carried between evaluations by the pure core).</summary>
    private DateTimeOffset? _turnaroundReadySinceUtc;
    private bool _turnaroundQuestionAsked;
    private bool _turnaroundDeclined;

    public GsxAutomationService(
        IGsxRemoteApi api,
        IGsxTriggerSlot slot,
        GsxServiceLifecycleTracker lifecycle,
        GsxGateSelectionService gateSelection,
        DepartureCycleState cycle,
        IFlightPhaseSource flightState,
        IProsimDataRefs prosim,
        ISimVars simVars,
        IGsxFlightPlanStatus flightPlan,
        ISimbriefImporter simbrief,
        OfpStore ofpStore,
        IOptionsMonitor<GsxOptions> options,
        GsxDiagnosticsStore diagnostics,
        GroundOpsSignals groundOpsSignals,
        GsxResyncState resyncState,
        JsonlEventLog eventLog,
        FuelConfirmationStore fuelConfirmation,
        IEfbInitOverrides initOverrides,
        DeiceRequestStore deiceRequest,
        TurnaroundStartStore turnaroundStart,
        ILogger<GsxAutomationService> logger)
    {
        ArgumentNullException.ThrowIfNull(fuelConfirmation);
        ArgumentNullException.ThrowIfNull(initOverrides);
        ArgumentNullException.ThrowIfNull(deiceRequest);
        ArgumentNullException.ThrowIfNull(turnaroundStart);
        _fuelConfirmation = fuelConfirmation;
        _initOverrides = initOverrides;
        _deiceRequest = deiceRequest;
        _turnaroundStart = turnaroundStart;
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(gateSelection);
        ArgumentNullException.ThrowIfNull(cycle);
        ArgumentNullException.ThrowIfNull(simbrief);
        ArgumentNullException.ThrowIfNull(flightState);
        ArgumentNullException.ThrowIfNull(simVars);
        ArgumentNullException.ThrowIfNull(flightPlan);
        _flightPlan = flightPlan;
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
        _slot = slot;
        _lifecycle = lifecycle;
        _gateSelection = gateSelection;
        _cycle = cycle;
        _flightState = flightState;
        _options = options;
        _diagnostics = diagnostics;
        _eventLog = eventLog;
        _logger = logger;

        _bookedSeatString = prosim.Subscribe(ProsimDataRefNames.PaxBookedString);
        _fuelTotal = prosim.Subscribe(ProsimDataRefNames.FuelTotal);
        _plannedFuel = prosim.Subscribe(ProsimDataRefNames.EfbPlannedFuel);
        _prosim = prosim;
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
        _pumpTimer = new Timer(_ => Pump(), null, PumpInterval, PumpInterval);
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
            _arrivalOfp = null;
            _arrivalOfpKnown = true;
            RecordDecision("turnaround", "recovered from tracking LVARs by the startup resync");
        }

        Pump();
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

        _turnaroundDeclined = true;
        RecordDecision(
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
        if (!MarkStarted(source))
        {
            return;
        }

        Pump();
    }

    /// <summary>The one place the cycle is marked started: records the source, settles an
    /// open ask-first question (any other trigger is an implicit yes) and raises the event.
    /// False when already started.</summary>
    private bool MarkStarted(string source)
    {
        if (_cycle.Started)
        {
            return false;
        }

        _cycle.MarkStarted();
        RecordDecision("departure sequence", $"started (source: {source})");
        _eventLog.Record("gsx-departure-started", new { source, turnaround = _cycle.IsTurnaround });
        _turnaroundStart.Accept(source);
        return true;
    }

    /// <summary>Single-shot force-next (INT/RAD / web button): the next evaluation bypasses the
    /// current step's activation rule — including Manual. Consumed by that evaluation whether or
    /// not anything could be called; the flight-plan gate is never bypassed.</summary>
    public void ForceNextService(string source)
    {
        if (!_cycle.Started || _cycle.Complete)
        {
            RecordDecision("force next service", $"{source}: ignored — departure sequence not running");
            return;
        }

        _forceNext = true;
        RecordDecision("force next service", $"requested by {source}");
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
        _pumpTimer.Dispose();
        _bookedSeatString.Dispose();
        _fuelTotal.Dispose();
        _plannedFuel.Dispose();
        DisposeSmartButton();
        _pumpLock.Dispose();
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
                _cycle.BeginTurnaroundCycle();
                _paxTargetArmed = false;
                _tankeringPrelimRaised = false;
                _planAvailableSinceUtc = null;
                _forceNext = false;
                // Turnaround auto-start bookkeeping (issue #169): the plan we flew in on is
                // the baseline — the next leg's OFP is whatever replaces it.
                _arrivalOfp = _ofpStore.Current;
                _arrivalOfpKnown = true;
                _turnaroundReadySinceUtc = null;
                _turnaroundQuestionAsked = false;
                _turnaroundDeclined = false;
                _slot.Reset("service cycles reset after arrival");
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

        // Issue #166: the de-icing policy runs when the DeIce step's turn comes. The store is
        // marked AFTER the pump lock is released — its observers pump again, and a nested
        // pump would bounce off the lock and lose the verdict until the next unrelated edge.
        var markDeiceNext = false;

        try
        {
            var options = _options.CurrentValue;

            // Plan-figure settling for the tankering decision (#118): stamp when the plan
            // first appears; the EFB fallback figure is trusted once the OFP is imported or
            // the settle window has passed with no import landing.
            if (!_flightPlan.FlightPlanAvailable)
            {
                _planAvailableSinceUtc = null;
            }
            else
            {
                _planAvailableSinceUtc ??= DateTimeOffset.UtcNow;
            }
            var planFiguresSettled = _flightPlan.OfpImported
                || (_planAvailableSinceUtc is { } since && DateTimeOffset.UtcNow - since >= TankeringPlanSettle);

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

            // The de-icing policy (2026-10-09) shapes the step list for THIS cycle: a requested
            // de-ice wakes a skipped DeIce step (or appends one last); an open captain's
            // question keeps the step on the board, held; a declined one skips it.
            var now = DateTimeOffset.UtcNow;
            var deice = _deiceRequest.Snapshot();
            var steps = DeiceRequestPolicy.EffectiveSteps(options.DepartureServices, deice, now);

            // All policy — gates, ordering, sequencing — is the pure core (campaign #78);
            // this shell only gathers inputs and performs the outcome's effects.
            var outcome = DepartureAutomationCore.Evaluate(new DepartureAutomationCore.PumpInputs(
                AutomationEnabled: options.AutomationEnabled,
                GsxReady: _api.Readiness == GsxReadiness.Ready,
                ResyncAssessed: _resyncState.IsAssessed,
                AutoStartOption: options.AutoStartDepartureServices,
                Phase: Phase,
                CycleStarted: _cycle.Started,
                CycleComplete: _cycle.Complete,
                PrepComplete: _cycle.PrepComplete,
                RequireOfp: options.RequireOfpBeforeDeparture,
                FmsPlanPresent: _flightPlan.FmsPlanPresent,
                OfpImported: _flightPlan.OfpImported,
                FlightPlanAvailable: _flightPlan.FlightPlanAvailable,
                FmsOrigin: _flightPlan.FmsOrigin,
                FmsDestination: _flightPlan.FmsDestination,
                Forced: _forceNext,
                InFlightServiceId: _slot.InFlightServiceId,
                IsTurnaround: _cycle.IsTurnaround,
                IsCompanyHub: IsCompanyHub(options),
                EstimatedEnroute: _ofpStore.Current?.EstimatedEnroute,
                Steps: steps,
                MirrorServices: _api.Mirror.Services,
                Cycle: Cycle,
                PreSkip: id => id.Equals(GsxServiceIds.Refueling, StringComparison.OrdinalIgnoreCase)
                    ? Sync.RefuelCore.TankeringSkipReason(
                        options.SkipRefuelOnTankering,
                        _flightPlan.FlightPlanAvailable,
                        planFiguresSettled,
                        _fuelTotal.Value,
                        // The crew's INIT FUEL RAMP override is the plan when set (2026-09-19).
                        EffectiveBlockFuel.PlanKg(_initOverrides.Snapshot(), _ofpStore.Current),
                        _plannedFuel.Value)
                    : id.Equals(GsxServiceIds.DeIce, StringComparison.OrdinalIgnoreCase)
                        ? DeiceRequestPolicy.SkipReason(deice)
                        : null,
                VoiceActivationMode: Sync.GsxGroundPrepCoordinator.IsVoiceActivation(options.GroundPrepActivation),
                // Real-world SOP (2026-09-19): with gsx.refuelCall = onFuelConfirmed the truck
                // waits for the crew's block-fuel confirmation; every other service proceeds.
                // The DeIce step waits the same way for the captain's de-icing answer.
                PreHold: id => id.Equals(GsxServiceIds.Refueling, StringComparison.OrdinalIgnoreCase)
                    && GsxOptions.IsRefuelOnFuelConfirmed(options.RefuelCall)
                    && !_fuelConfirmation.Confirmed
                    ? FuelConfirmationHoldReason
                    : id.Equals(GsxServiceIds.DeIce, StringComparison.OrdinalIgnoreCase)
                        ? DeiceRequestPolicy.HoldReason(deice, now)
                        : null,
                Turnaround: new DepartureAutomationCore.TurnaroundStartInputs(
                    Enabled: GsxOptions.IsVoiceThenAuto(options.GroundPrepActivation),
                    NextLegOfpLoaded: NextLegOfpLoaded(),
                    ReadySinceUtc: _turnaroundReadySinceUtc,
                    Now: now,
                    Delay: TimeSpan.FromSeconds(Math.Max(0, options.TurnaroundAutoStartDelaySeconds)),
                    AskFirst: options.TurnaroundAskFirst,
                    QuestionAsked: _turnaroundQuestionAsked,
                    Declined: _turnaroundDeclined)));

            // Settle stamp for the next evaluation: the core reports when the turnaround
            // conditions first held (or null once they lapse).
            if (_turnaroundReadySinceUtc is null && outcome.TurnaroundReadySinceUtc is { } readySince)
            {
                RecordDecision(
                    "departure sequence",
                    $"turnaround with the next plan loaded — auto-start in {Math.Max(0, options.TurnaroundAutoStartDelaySeconds)} s"
                    + (options.TurnaroundAskFirst ? " (the FO will ask first)" : ""));
                _turnaroundReadySinceUtc = readySince;
            }
            else if (outcome.TurnaroundReadySinceUtc is null)
            {
                _turnaroundReadySinceUtc = null;
            }

            if (outcome.AutoStartSource is { } autoSource)
            {
                MarkStarted(autoSource);
                RecordDecision(
                    "departure sequence",
                    autoSource == DepartureAutomationCore.TurnaroundAutoStartSource
                        ? "auto-started (turnaround)"
                        : "auto-started (Preparation phase)");
            }

            if (outcome.AskTurnaroundStart && !_turnaroundQuestionAsked)
            {
                _turnaroundQuestionAsked = true;
                RecordDecision("departure sequence", "turnaround — asking the captain to commence ground services");
                _eventLog.Record("gsx-turnaround-question", new { prompt = TurnaroundStartPrompt });
                _turnaroundStart.Ask(TurnaroundStartPrompt);
            }

            if (outcome.HoldReason is not null)
            {
                RecordDecisionOnce("hold departure services", outcome.HoldReason);
            }

            if (outcome.WaitingBoard is not null)
            {
                PublishWaitingBoard(outcome.WaitingBoard);
                return;
            }

            if (outcome.Plan is not { } plan)
            {
                return; // gated before sequencing with no board to publish (disabled / complete)
            }

            if (!deice.StepNext
                && plan.HeldAtTurn.Any(id => id.Equals(GsxServiceIds.DeIce, StringComparison.OrdinalIgnoreCase)))
            {
                markDeiceNext = true;
                RecordDecisionOnce("de-ice step next", "every earlier service done — the de-icing policy decides now");
            }

            if (outcome.StartSimbriefImport)
            {
                TryStartSimbriefImport();
            }
            if (outcome.PlanDiagnostic is not null)
            {
                RecordDecisionOnce("flight plan detection", outcome.PlanDiagnostic);
            }
            if (outcome.ArmPaxTarget)
            {
                ArmGsxPaxTarget();
            }

            if (outcome.ConsumedForce)
            {
                _forceNext = false; // single-shot, consumed by this evaluation
                RecordDecision(
                    "force next service",
                    plan.Trigger is not null ? $"calling {plan.Trigger}" : "nothing eligible to force right now");
            }

            PublishBoard(steps, plan);

            foreach (var (serviceId, reason) in plan.Skipped)
            {
                RecordDecisionOnce($"skip {serviceId}", reason);

                // Tankering skip (#117): the truck is never ordered, so the refuel-active
                // edge that fires the preliminary loadsheet never happens — raise it here
                // once per cycle so autoPrelimOnRefuel still produces the prelim.
                if (serviceId.Equals(GsxServiceIds.Refueling, StringComparison.OrdinalIgnoreCase)
                    && reason.Contains("tankering", StringComparison.OrdinalIgnoreCase)
                    && !_tankeringPrelimRaised)
                {
                    _tankeringPrelimRaised = true;
                    RecordDecision("refuel skipped for tankering", "preliminary loadsheet trigger raised in place of the refuel-active edge");
                    _groundOpsSignals.RaiseRefuelServiceActive();
                }
            }

            foreach (var (serviceId, reason) in plan.Holds)
            {
                RecordDecisionOnce($"hold {serviceId}", reason);
            }

            if (outcome.Trigger is { } trigger)
            {
                // A tankering skip is a live judgement, not a latch — when the inputs
                // materially change (2026-09-05: the pilot reset the FOB to ~300 kg after the
                // skip had been published), calling Refueling after all is CORRECT, but the
                // flight log must say why the skip reversed instead of contradicting itself
                // silently (#118).
                if (trigger.Equals(GsxServiceIds.Refueling, StringComparison.OrdinalIgnoreCase)
                    && _tankeringPrelimRaised)
                {
                    RecordDecisionOnce(
                        "tankering skip reversed",
                        $"FOB {_fuelTotal.Value:F0} kg no longer meets the plan — Refueling called after all");
                }

                int attempt;
                lock (_triggerAttempts)
                {
                    attempt = _triggerAttempts[trigger] = _triggerAttempts.GetValueOrDefault(trigger) + 1;
                }
                // No watcher retry: a dropped call frees the slot and this pump re-offers the
                // same service — the sequencer IS the retry-forever policy. SlotWait zero so
                // the pump never stalls waiting on an on-demand caller's trigger.
                var reason = attempt == 1
                    ? plan.TriggerReason ?? "next in departure order"
                    : $"{plan.TriggerReason} (attempt {attempt})";
                _ = _slot.TryDispatchAsync(new GsxTriggerRequest(trigger, $"sequencer — {reason}")
                {
                    SlotWait = TimeSpan.Zero,
                });
            }

            if (outcome.AllDone)
            {
                _cycle.MarkComplete();
                RecordDecision("departure sequence", "all departure services completed or skipped");
                _eventLog.Record("gsx-departure-complete");
                _groundOpsSignals.RaiseDepartureServicesCompleted(); // "ready for pushback" (#151)
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

        if (markDeiceNext)
        {
            _deiceRequest.MarkStepNext();
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
    private void ArmGsxPaxTarget() => _ = TryArmGsxPaxTargetAsync();

    /// <summary>The booked passenger count from ProSim's seat map — 0 while the OFP has not
    /// written the manifest yet.</summary>
    private int BookedPax()
        => ProsimCompanion.Core.Aircraft.SeatMap.Parse(_bookedSeatString.Value).Count(seat => seat);

    /// <summary>Arms the pax target once per cycle and reports whether it is armed now (already
    /// armed counts). False = the booked map is not written yet, or MSFS is not connected —
    /// the sequencer retries on a later pump; an on-demand Boarding call must not proceed.</summary>
    private async Task<bool> TryArmGsxPaxTargetAsync()
    {
        if (_paxTargetArmed)
        {
            return true;
        }

        var booked = BookedPax();
        if (booked <= 0)
        {
            return false; // booked map not written yet — retry on a later pump
        }

        _paxTargetArmed = true;
        try
        {
            await _simVars.WriteAsync(GsxLvarNames.NumPassengers.Name, booked).ConfigureAwait(false);
            RecordDecision("pax target", $"armed GSX with {booked} passengers (booked manifest)");

            if (_options.CurrentValue.SkipCrewBoardingQuestion)
            {
                await _simVars.WriteAsync(GsxLvarNames.CrewNotBoarding, 1).ConfigureAwait(false);
                await _simVars.WriteAsync(GsxLvarNames.PilotsNotBoarding, 1).ConfigureAwait(false);
                await _simVars.WriteAsync(GsxLvarNames.CrewNotDeboarding, 1).ConfigureAwait(false);
                await _simVars.WriteAsync(GsxLvarNames.PilotsNotDeboarding, 1).ConfigureAwait(false);
                RecordDecision("pax target", "crew/pilot boarding questions suppressed via LVARs");
            }

            return true;
        }
        catch (InvalidOperationException ex)
        {
            _paxTargetArmed = false; // MSFS not connected yet — retry on a later pump
            _logger.LogDebug("Pax target arming deferred: {Reason}", ex.Message);
            return false;
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

        var armed = await TryArmGsxPaxTargetAsync().ConfigureAwait(false);
        if (requirePaxTarget && !armed)
        {
            return DepartureStartForCallOutcome.Refused(
                BookedPax() <= 0
                    ? "No flight plan loaded yet — load the OFP first (the passenger manifest is empty)."
                    : "The passenger count could not be sent to GSX yet (MSFS not connected) — try again in a moment.");
        }

        if (MarkStarted(source))
        {
            Pump();
        }

        return DepartureStartForCallOutcome.Ok;
    }

    /// <summary>True once the OFP store holds a plan other than the one the aircraft arrived
    /// with (issue #169). Before any arrival this session nothing counts — the first leg stays
    /// on the voice gate by design.</summary>
    private bool NextLegOfpLoaded()
    {
        if (!_arrivalOfpKnown || _ofpStore.Current is not { } current)
        {
            return false;
        }

        if (_arrivalOfp is null)
        {
            return true;
        }

        if (ReferenceEquals(current, _arrivalOfp))
        {
            return false;
        }

        // Same SimBrief request id = the same plan re-imported (or re-fetched), not a new leg.
        return current.RequestId.Length == 0
            || !string.Equals(current.RequestId, _arrivalOfp.RequestId, StringComparison.Ordinal);
    }

    /// <summary>The FO's ask-first question text (issue #169). Probed by
    /// <c>turnaround-auto-start</c> — keep the wording stable.</summary>
    internal const string TurnaroundStartPrompt = "Next plan loaded — commence ground services?";

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
                var outcome = await _simbrief.TryImportAsync(source: "automation (MCDU plan detected)").ConfigureAwait(false);
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
        var inFlight = _slot.InFlightServiceId;

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
