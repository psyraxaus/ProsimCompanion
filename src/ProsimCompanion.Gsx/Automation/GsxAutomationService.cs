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
    private readonly IDataRefSubscription _ofpImported;
    private readonly IDataRefSubscription _fmsOrigin;
    private readonly IDataRefSubscription _fmsDestination;
    private readonly Timer _pumpTimer;
    private readonly SemaphoreSlim _pumpLock = new(1, 1);
    private readonly Dictionary<string, string> _lastReasonByAction = new(StringComparer.Ordinal);
    private volatile bool _departureStarted;
    private volatile bool _departureComplete;
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
        _simbrief = simbrief;
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

        _flightState.PhaseChanged += OnFlightPhaseChanged;
        _lifecycle.ServiceEvent += OnServiceEvent;
        _api.Mirror.Updated += OnMirrorUpdated;
        _pumpTimer = new Timer(_ => Pump(), null, PumpInterval, PumpInterval);
    }

    /// <summary>Current automation phase (derived, never re-computed by features).</summary>
    public GsxAutomationPhase Phase { get; private set; } = GsxAutomationPhase.SessionStart;

    /// <summary>True once the departure sequence has been started (manually or automatically).</summary>
    public bool DepartureStarted => _departureStarted;

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
                return;
            }

            // Order (owner-specified): reposition → GPU/chocks → jetway/stairs must all finish
            // before any departure service is called.
            if (!_groundPrep.PrepComplete)
            {
                RecordDecisionOnce("hold departure services", "waiting for ground preparation (reposition/equipment/jetway) to complete");
                return;
            }

            // Flight plan = SimBrief OFP imported into the EFB, OR a plan in the MCDU (origin +
            // destination set). When neither exists, the SimBrief importer is invoked (60 s
            // cooldown) — the predecessors' aircraft-loading mechanism, which also supplies the
            // booked seat map and planned fuel/cargo.
            var ofpImported = _ofpImported.GetValue(false);
            var fmsOrigin = _fmsOrigin.GetValue<string?>(null);
            var fmsDestination = _fmsDestination.GetValue<string?>(null);
            var flightPlanAvailable = ofpImported
                || (!string.IsNullOrWhiteSpace(fmsOrigin) && !string.IsNullOrWhiteSpace(fmsDestination));
            if (!ofpImported && options.RequireOfpBeforeDeparture)
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

            var plan = DepartureSequencer.Next(
                options.DepartureServiceOrder,
                _api.Mirror.Services,
                _lifecycle.IsCompleted,
                flightPlanAvailable,
                options.RequireOfpBeforeDeparture,
                options.ConcurrentServices,
                options.BoardingAfter);

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

    /// <summary>Fires the SimBrief import in the background with a 60 s cooldown; the importer
    /// itself decision-logs its progress, and a success re-pumps the sequencer.</summary>
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
