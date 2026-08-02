using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Gsx.Gate;
using ProsimCompanion.Gsx.Services;

namespace ProsimCompanion.Gsx.Automation;

/// <summary>
/// The ground-automation coordinator: tracks the automation phase from the central flight state
/// engine, runs the one-at-a-time departure service sequence (OFP-gated), arms the configured
/// arrival gate when reaching flight, writes handler.set autoSelectOperator once per gate
/// session, and resets service cycles on arrival. Every decision — including holds and skips —
/// is recorded with its reason (decision log + session event log), deduplicated so a stable
/// state does not spam.
/// </summary>
public sealed class GsxAutomationService : IDisposable
{
    private static readonly TimeSpan PumpInterval = TimeSpan.FromSeconds(3);

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
    private readonly Timer _pumpTimer;
    private readonly SemaphoreSlim _pumpLock = new(1, 1);
    private readonly Dictionary<string, string> _lastReasonByAction = new(StringComparer.Ordinal);
    private volatile bool _departureStarted;
    private volatile bool _departureComplete;
    private bool _paxTargetArmed;
    private string? _autoSelectArmedKey;
    private DateTimeOffset _lastImportAttempt = DateTimeOffset.MinValue;

    private readonly ISimbriefImporter _simbrief;

    public GsxAutomationService(
        IGsxRemoteApi api,
        GsxServiceLifecycleTracker lifecycle,
        GsxGateSelectionService gateSelection,
        Sync.GsxGroundPrepCoordinator groundPrep,
        FlightStateEngine flightState,
        IProsimDataRefs prosim,
        ISimVars simVars,
        ISimbriefImporter simbrief,
        IOptionsMonitor<GsxOptions> options,
        GsxDiagnosticsStore diagnostics,
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
        _simbrief = simbrief;
        _simVars = simVars;
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

        _flightState.PhaseChanged += OnFlightPhaseChanged;
        _lifecycle.ServiceEvent += OnServiceEvent;
        _api.Mirror.Updated += OnMirrorUpdated;
        _pumpTimer = new Timer(_ => Pump(), null, PumpInterval, PumpInterval);
    }

    /// <summary>Current automation phase (derived, never re-computed by features).</summary>
    public GsxAutomationPhase Phase { get; private set; } = GsxAutomationPhase.SessionStart;

    /// <summary>True once the departure sequence has been started (manually or automatically).</summary>
    public bool DepartureStarted => _departureStarted;

    /// <summary>True once every departure service completed or was skipped — the
    /// beacon-orchestrated pushback sequence arms on this.</summary>
    public bool DepartureComplete => _departureComplete;

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

    public void Dispose()
    {
        _flightState.PhaseChanged -= OnFlightPhaseChanged;
        _lifecycle.ServiceEvent -= OnServiceEvent;
        _api.Mirror.Updated -= OnMirrorUpdated;
        _pumpTimer.Dispose();
        _ofpImported.Dispose();
        _fmsOrigin.Dispose();
        _fmsDestination.Dispose();
        _bookedSeatString.Dispose();
        _pumpLock.Dispose();
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
                // New turnaround coming: fresh service cycles, fresh departure sequence.
                _lifecycle.ResetCycle();
                _departureStarted = false;
                _departureComplete = false;
                _paxTargetArmed = false;
                RecordDecision("turnaround", "service cycles reset after arrival");
                break;
        }

        Pump();
    }

    private void OnServiceEvent(string serviceId, GsxServiceLifecycleEvent lifecycleEvent)
    {
        if (lifecycleEvent == GsxServiceLifecycleEvent.Completed)
        {
            Pump();
        }
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

            var plan = DepartureSequencer.Next(
                options.DepartureServiceOrder,
                _api.Mirror.Services,
                _lifecycle.IsCompleted,
                _lifecycle.IsPending,
                flightPlanAvailable,
                options.RequireOfpBeforeDeparture,
                options.ConcurrentServices,
                options.BoardingAfter);

            PublishBoard(options.DepartureServiceOrder, plan);

            foreach (var (serviceId, reason) in plan.Skipped)
            {
                RecordDecisionOnce($"skip {serviceId}", reason);
            }

            foreach (var (serviceId, reason) in plan.Holds)
            {
                RecordDecisionOnce($"hold {serviceId}", reason);
            }

            foreach (var serviceId in plan.Trigger)
            {
                RecordDecision($"trigger {serviceId}", options.ConcurrentServices ? "callable (concurrent mode)" : "next in departure order");
                _lifecycle.MarkCalled(serviceId);
                _ = TriggerServiceAsync(serviceId);
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

    private async Task TriggerServiceAsync(string serviceId)
    {
        var result = await _api.SendCommandAsync(
            "service.trigger",
            new JsonObject { ["service"] = serviceId }).ConfigureAwait(false);
        if (!result.Ok)
        {
            RecordDecision($"trigger {serviceId}", $"rejected ({result.Code})");
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
    /// mirror states and lifecycle cycles, in configured service order.</summary>
    private void PublishBoard(IReadOnlyList<string> order, DeparturePlan plan)
    {
        var holds = plan.Holds.ToDictionary(h => h.ServiceId, h => h.Reason, StringComparer.OrdinalIgnoreCase);
        var skips = plan.Skipped.ToDictionary(s => s.ServiceId, s => s.Reason, StringComparer.OrdinalIgnoreCase);
        var triggered = new HashSet<string>(plan.Trigger, StringComparer.OrdinalIgnoreCase);
        var services = _api.Mirror.Services;

        var rows = new List<GsxServiceBoardRow>();
        foreach (var id in order.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            services.TryGetValue(id, out var info);
            var row = _lifecycle.IsCompleted(id) ? new GsxServiceBoardRow(id, GsxServiceStage.Completed, null)
                : info?.State == Protocol.GsxServiceState.Active ? new GsxServiceBoardRow(id, GsxServiceStage.Active, info.ProgressText)
                : info?.State == Protocol.GsxServiceState.Requested ? new GsxServiceBoardRow(id, GsxServiceStage.Requested, null)
                : triggered.Contains(id) || _lifecycle.IsPending(id) ? new GsxServiceBoardRow(id, GsxServiceStage.Called, null)
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
        var rows = _options.CurrentValue.DepartureServiceOrder
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(id => _lifecycle.IsCompleted(id)
                ? new GsxServiceBoardRow(id, GsxServiceStage.Completed, null)
                : new GsxServiceBoardRow(id, GsxServiceStage.Waiting, reason))
            .ToList();
        _diagnostics.UpdateServiceBoard(rows);
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
