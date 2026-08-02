using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.State;
using ProsimCompanion.Gsx.Menu;
using ProsimCompanion.Gsx.Protocol;
using ProsimCompanion.Gsx.Services;

namespace ProsimCompanion.Gsx;

/// <summary>
/// Wires the GSX layers together: mirror service updates feed the lifecycle tracker; a periodic
/// reconcile tick re-processes current state to catch missed edges; a Couatl restart (sid
/// change) is surfaced; lifecycle events are recorded in the session event log.
/// </summary>
public sealed class GsxBootstrapService : IHostedService, IDisposable
{
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(5);

    private readonly GsxRemoteApiClient _client;
    private readonly GsxServiceLifecycleTracker _lifecycle;
    private readonly GsxQuestionDispatcher _questions;
    private readonly GsxDiagnosticsStore _diagnostics;
    private readonly Gate.GsxGateSelectionService _gateSelection;
    private readonly Automation.GsxAutomationService _automation;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<GsxBootstrapService> _logger;
    private readonly HashSet<string> _reportedUnknownKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _reportedUnknownStates = new(StringComparer.OrdinalIgnoreCase);
    private Timer? _reconcileTimer;

    public GsxBootstrapService(
        GsxRemoteApiClient client,
        GsxServiceLifecycleTracker lifecycle,
        GsxQuestionDispatcher questions,
        GsxQuestionCatalog questionCatalog,
        GsxDiagnosticsStore diagnostics,
        Gate.GsxGateSelectionService gateSelection,
        Automation.GsxAutomationService automation,
        // The sync modules are injected purely to activate their event wiring at startup.
        Sync.GsxRefuelSync refuelSync,
        Sync.GsxBoardingSync boardingSync,
        Sync.GsxGroundEquipmentService groundEquipment,
        Sync.GsxJetwayStairsService jetwayStairs,
        Sync.GsxRepositionService reposition,
        Sync.GsxGroundPrepCoordinator groundPrep,
        Sync.ProsimNativeGsxGuard nativeGsxGuard,
        Sync.GsxDoorService doors,
        Sync.GsxPushbackSequenceService pushbackSequence,
        Sync.GsxArrivalService arrival,
        JsonlEventLog eventLog,
        ILogger<GsxBootstrapService> logger)
    {
        ArgumentNullException.ThrowIfNull(doors);
        ArgumentNullException.ThrowIfNull(pushbackSequence);
        ArgumentNullException.ThrowIfNull(arrival);
        ArgumentNullException.ThrowIfNull(questionCatalog);
        ArgumentNullException.ThrowIfNull(gateSelection);
        ArgumentNullException.ThrowIfNull(automation);
        ArgumentNullException.ThrowIfNull(refuelSync);
        ArgumentNullException.ThrowIfNull(boardingSync);
        ArgumentNullException.ThrowIfNull(groundEquipment);
        ArgumentNullException.ThrowIfNull(jetwayStairs);
        ArgumentNullException.ThrowIfNull(reposition);
        ArgumentNullException.ThrowIfNull(groundPrep);
        ArgumentNullException.ThrowIfNull(nativeGsxGuard);
        ArgumentNullException.ThrowIfNull(client);
        _gateSelection = gateSelection;
        _automation = automation;
        questionCatalog.RegisterAll(questions);
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(questions);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _client = client;
        _lifecycle = lifecycle;
        _questions = questions;
        _diagnostics = diagnostics;
        _eventLog = eventLog;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _client.Mirror.Updated += OnMirrorUpdated;
        _client.Mirror.SidChanged += OnSidChanged;
        _client.Mirror.UnknownKeySeen += OnUnknownKey;
        _client.ReadinessChanged += OnReadinessChanged;
        _client.CommandCompleted += _diagnostics.RecordCommand;
        _gateSelection.Changed += PublishDiagnostics;
        _lifecycle.ServiceEvent += OnServiceEvent;

        _reconcileTimer = new Timer(
            _ => _lifecycle.Process(_client.Mirror.Services),
            null,
            ReconcileInterval,
            ReconcileInterval);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _client.Mirror.Updated -= OnMirrorUpdated;
        _client.Mirror.SidChanged -= OnSidChanged;
        _client.Mirror.UnknownKeySeen -= OnUnknownKey;
        _client.ReadinessChanged -= OnReadinessChanged;
        _client.CommandCompleted -= _diagnostics.RecordCommand;
        _gateSelection.Changed -= PublishDiagnostics;
        _lifecycle.ServiceEvent -= OnServiceEvent;
        _reconcileTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        return Task.CompletedTask;
    }

    public void Dispose() => _reconcileTimer?.Dispose();

    private void OnMirrorUpdated(string key)
    {
        if (string.Equals(key, "services", StringComparison.OrdinalIgnoreCase))
        {
            _lifecycle.Process(_client.Mirror.Services);
            ReportUnknownSemanticStates();
        }
        else if (key is "menu" or "menuShown")
        {
            // Fire-and-forget is safe: the dispatcher contains all its own failures.
            _ = _questions.OnMenuUpdatedAsync(_client.Mirror.MenuShown, _client.Mirror.Menu?.Title);
        }

        PublishDiagnostics();
    }

    private void OnReadinessChanged(GsxReadiness readiness)
    {
        _eventLog.Record("gsx-readiness", new { readiness = readiness.ToString(), capabilities = _client.Capabilities });
        PublishDiagnostics();
    }

    /// <summary>Keys confirmed present in live GSX 4 sessions (2026-08-02) that this client
    /// deliberately does not consume — logged at Debug, not Warning.</summary>
    private static readonly HashSet<string> KnownIgnoredKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "state", "stateText", "airline", "commandIcons", "commandIconsSvg", "simbrief",
        "statusHtml", "settings", "search", "gateProperties", "operators", "receipt",
        "billing", "prompts",
    };

    /// <summary>First-flight telemetry: a state key the mirror does not consume, reported once
    /// per key so a protocol addition is noticed in the smoke-test log.</summary>
    private void OnUnknownKey(string key)
    {
        lock (_reportedUnknownKeys)
        {
            if (!_reportedUnknownKeys.Add(key))
            {
                return;
            }
        }

        if (KnownIgnoredKeys.Contains(key))
        {
            _logger.LogDebug("GSX state key '{Key}' present (known, deliberately unconsumed)", key);
        }
        else
        {
            _logger.LogWarning("GSX state model carries key '{Key}' this client does not consume — protocol addition?", key);
        }
    }

    /// <summary>First-flight telemetry: semantic state strings outside the documented set,
    /// reported once per value.</summary>
    private void ReportUnknownSemanticStates()
    {
        foreach (var service in _client.Mirror.Services.Values)
        {
            if (service.State != GsxServiceState.Unknown || string.IsNullOrEmpty(service.SemanticState))
            {
                continue;
            }

            lock (_reportedUnknownStates)
            {
                if (!_reportedUnknownStates.Add(service.SemanticState))
                {
                    continue;
                }
            }
            _logger.LogWarning(
                "GSX service {Service} reports undocumented semantic state '{State}'",
                service.Id,
                service.SemanticState);
        }
    }

    private void PublishDiagnostics()
    {
        var mirror = _client.Mirror;
        _diagnostics.Update(new GsxDiagnosticsSnapshot(
            _client.Readiness.ToString(),
            [.. _client.Capabilities],
            mirror.AirportIcao,
            mirror.GateContextKey,
            mirror.StartupSid,
            mirror.MenuShown,
            mirror.Menu?.Title,
            mirror.Menu?.Entries ?? [],
            [.. mirror.Services.Values.Select(service => new GsxServiceView(
                service.Id,
                service.DisplayName,
                service.SemanticState,
                service.State.ToString(),
                service.CanTrigger,
                service.Waiting,
                service.ProgressText))],
            [])
        {
            AutomationPhase = _automation.Phase.ToString(),
            GateRequest = _gateSelection.RequestedGate is null
                ? null
                : $"{_gateSelection.RequestedGate}: {_gateSelection.Status} — {_gateSelection.StatusDetail}",
        });
    }

    private void OnSidChanged(string? oldSid, string? newSid)
    {
        // Engine restart: cached context is gone. Service cycles keep their latches — the next
        // snapshot re-syncs states and the idempotent processing avoids duplicate events.
        _logger.LogWarning("Couatl engine restarted (sid {Old} -> {New}); cached GSX context invalidated", oldSid, newSid);
        _eventLog.Record("gsx-engine-restarted", new { oldSid, newSid });
    }

    private void OnServiceEvent(string serviceId, GsxServiceLifecycleEvent lifecycleEvent)
        => _eventLog.Record("gsx-service", new { service = serviceId, @event = lifecycleEvent.ToString() });
}
