using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Gsx.Protocol;
using ProsimCompanion.Gsx.Services;

namespace ProsimCompanion.Gsx.Sync;

/// <summary>
/// Connects the jetway — or calls stairs at jetway-less gates — automatically, once per gate
/// session, during ground preparation. State is checked first so an already-connected jetway is
/// never toggled (the trigger is a toggle!): a mirror state of Completed/Active counts as
/// connected, and the raw GSX LVARs are decision-logged alongside every decision so their live
/// semantics accumulate for refinement.
/// </summary>
public sealed class GsxJetwayStairsService : IDisposable
{
    private const string JetwayServiceId = "OperateJetways";
    private const string StairsServiceId = "OperateStairs";

    private readonly IGsxRemoteApi _api;
    private readonly GsxServiceLifecycleTracker _lifecycle;
    private readonly FlightStateEngine _flightState;
    private readonly IOptionsMonitor<GsxOptions> _options;
    private readonly GsxDiagnosticsStore _diagnostics;
    private readonly ILogger<GsxJetwayStairsService> _logger;
    private readonly IDataRefSubscription _jetwayLvar;
    private readonly IDataRefSubscription _stairsLvar;
    private readonly IDataRefSubscription _operateJetwaysState;
    private readonly IDataRefSubscription _operateStairsState;
    private readonly Timer _timer;
    private string? _handledGateKey;
    private string? _currentGateKey;
    private string? _pendingServiceId;
    private DateTimeOffset _pendingDeadline;
    private bool _fallbackTried;
    private int _checking;

    /// <summary>L:FSDT_GSX_JETWAY value meaning "no jetway exists at this position" — live GSX 4
    /// session 2026-08-02: a remote stand read jetway=2 while the mirror still offered a
    /// (non-functional) OperateJetways service.</summary>
    private const int JetwayLvarNotPresent = 2;

    public GsxJetwayStairsService(
        IGsxRemoteApi api,
        GsxServiceLifecycleTracker lifecycle,
        FlightStateEngine flightState,
        ISimVars simVars,
        IOptionsMonitor<GsxOptions> options,
        GsxDiagnosticsStore diagnostics,
        ILogger<GsxJetwayStairsService> logger)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(flightState);
        ArgumentNullException.ThrowIfNull(simVars);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(logger);

        _api = api;
        _lifecycle = lifecycle;
        _flightState = flightState;
        _options = options;
        _diagnostics = diagnostics;
        _logger = logger;

        _jetwayLvar = simVars.Subscribe(GsxLvarNames.Jetway, "number", DataRefTier.Normal);
        _stairsLvar = simVars.Subscribe(GsxLvarNames.Stairs, "number", DataRefTier.Normal);
        _operateJetwaysState = simVars.Subscribe(GsxLvarNames.OperateJetwaysState, "number", DataRefTier.Normal);
        _operateStairsState = simVars.Subscribe(GsxLvarNames.OperateStairsState, "number", DataRefTier.Normal);

        _api.Mirror.SidChanged += OnSidChanged;
        _timer = new Timer(_ => Check(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    public void Dispose()
    {
        _api.Mirror.SidChanged -= OnSidChanged;
        _timer.Dispose();
        _jetwayLvar.Dispose();
        _stairsLvar.Dispose();
        _operateJetwaysState.Dispose();
        _operateStairsState.Dispose();
    }

    private void OnSidChanged(string? oldSid, string? newSid) => _handledGateKey = null;

    private void Check()
    {
        if (Interlocked.Exchange(ref _checking, 1) == 1)
        {
            return;
        }

        try
        {
            var options = _options.CurrentValue;
            var gateKey = _api.Mirror.GateContextKey;
            if (!options.AutomationEnabled
                || !options.AutoConnectJetwayOrStairs
                || _api.Readiness != GsxReadiness.Ready
                || gateKey is null
                || _flightState.CurrentPhase is not (FlightPhase.Preflight or FlightPhase.ColdAndDark))
            {
                return;
            }

            // Fresh gate context resets the whole cycle.
            if (!string.Equals(gateKey, _currentGateKey, StringComparison.Ordinal))
            {
                _currentGateKey = gateKey;
                _handledGateKey = null;
                _pendingServiceId = null;
                _fallbackTried = false;
            }

            if (_pendingServiceId is not null)
            {
                VerifyPendingTrigger(gateKey);
                return;
            }

            if (string.Equals(gateKey, _handledGateKey, StringComparison.Ordinal))
            {
                return;
            }

            var services = _api.Mirror.Services;
            if (services.Count == 0)
            {
                return; // mirror not populated yet — try again next tick
            }

            // Live GSX 4 lists OperateJetways even at jetway-less stands (and acks a trigger
            // that does nothing) — the jetway LVAR is the truth: 2 = no jetway here.
            var jetwayLvar = (int)_jetwayLvar.GetValue(0.0);
            var jetwayExists = services.ContainsKey(JetwayServiceId) && jetwayLvar != JetwayLvarNotPresent;
            var targetId = jetwayExists ? JetwayServiceId
                : services.ContainsKey(StairsServiceId) ? StairsServiceId
                : null;
            if (targetId is null)
            {
                _handledGateKey = gateKey;
                RecordDecision("jetway/stairs", $"nothing to connect at {gateKey} (jetway LVAR={jetwayLvar}, no stairs service)");
                return;
            }

            var target = services[targetId];
            var lvarDetail =
                $"LVARs jetway={jetwayLvar} stairs={_stairsLvar.GetValue(0.0):F0} " +
                $"opJetways={_operateJetwaysState.GetValue(0.0):F0} opStairs={_operateStairsState.GetValue(0.0):F0}";

            // Connected check: a docked jetway/stairs mirrors as Active or Completed (spec §4.3
            // — the mirror can read a docked jetway as completed). Never toggle those.
            if (target.State is GsxServiceState.Active or GsxServiceState.Completed)
            {
                _handledGateKey = gateKey;
                RecordDecision("jetway/stairs", $"{targetId} already connected ({target.SemanticState}); {lvarDetail}");
                return;
            }

            if (target.State != GsxServiceState.Callable || !target.CanTrigger)
            {
                _logger.LogDebug(
                    "Jetway/stairs waiting: {Service} state {State} canTrigger {CanTrigger}",
                    targetId,
                    target.SemanticState,
                    target.CanTrigger);
                return;
            }

            RecordDecision(
                "jetway/stairs",
                $"connecting {targetId} at {gateKey}{(jetwayExists ? "" : " (jetway LVAR=2 — no jetway at this stand)")}; {lvarDetail}");
            StartTrigger(targetId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Jetway/stairs check failed");
        }
        finally
        {
            Interlocked.Exchange(ref _checking, 0);
        }
    }

    /// <summary>A trigger is only trusted once the service shows a Requested/Active/Completed
    /// edge — GSX has been observed acking OperateJetways at a jetway-less stand and doing
    /// nothing. No edge within the deadline ⇒ fall back to the other service once.</summary>
    private void VerifyPendingTrigger(string gateKey)
    {
        var pending = _pendingServiceId!;
        var state = _api.Mirror.Services.GetValueOrDefault(pending)?.State;
        if (state is GsxServiceState.Requested or GsxServiceState.Active or GsxServiceState.Completed)
        {
            _pendingServiceId = null;
            _handledGateKey = gateKey;
            RecordDecision("jetway/stairs", $"{pending} responding ({state})");
            return;
        }

        if (DateTimeOffset.UtcNow < _pendingDeadline)
        {
            return;
        }

        _pendingServiceId = null;
        var fallbackId = pending == JetwayServiceId ? StairsServiceId : JetwayServiceId;
        if (!_fallbackTried && _api.Mirror.Services.TryGetValue(fallbackId, out var fallback)
            && fallback.State == GsxServiceState.Callable && fallback.CanTrigger)
        {
            _fallbackTried = true;
            RecordDecision("jetway/stairs", $"{pending} showed no response — falling back to {fallbackId}");
            StartTrigger(fallbackId);
        }
        else
        {
            _handledGateKey = gateKey;
            RecordDecision("jetway/stairs", $"{pending} showed no response and no usable fallback — giving up for this gate");
        }
    }

    private void StartTrigger(string serviceId)
    {
        _pendingServiceId = serviceId;
        _pendingDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        _lifecycle.MarkCalled(serviceId);
        _ = TriggerAsync(serviceId);
    }

    private async Task TriggerAsync(string serviceId)
    {
        var result = await _api.SendCommandAsync(
            "service.trigger",
            new JsonObject { ["service"] = serviceId }).ConfigureAwait(false);
        if (!result.Ok)
        {
            // Let the pending verification time out into the fallback path.
            RecordDecision("jetway/stairs", $"{serviceId} trigger rejected ({result.Code})");
        }
    }

    private void RecordDecision(string action, string reason)
    {
        _logger.LogInformation("GSX {Action}: {Reason}", action, reason);
        _diagnostics.RecordDecision(new GsxDecisionView(DateTimeOffset.UtcNow, action, reason));
    }
}
