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
    private int _checking;

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
                || string.Equals(gateKey, _handledGateKey, StringComparison.Ordinal)
                || _flightState.CurrentPhase is not (FlightPhase.Preflight or FlightPhase.ColdAndDark))
            {
                return;
            }

            var services = _api.Mirror.Services;
            if (services.Count == 0)
            {
                return; // mirror not populated yet — try again next tick
            }

            // Prefer the jetway when the gate offers one; stairs otherwise.
            var hasJetway = services.TryGetValue(JetwayServiceId, out var jetway);
            var target = hasJetway ? jetway : services.GetValueOrDefault(StairsServiceId);
            var targetId = hasJetway ? JetwayServiceId : StairsServiceId;
            if (target is null)
            {
                _handledGateKey = gateKey;
                RecordDecision("jetway/stairs", $"no jetway or stairs service offered at {gateKey}");
                return;
            }

            var lvarDetail =
                $"LVARs jetway={_jetwayLvar.GetValue(0.0):F0} stairs={_stairsLvar.GetValue(0.0):F0} " +
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
                // Not ready yet — keep waiting (no gate latch), the next tick re-checks.
                _logger.LogDebug(
                    "Jetway/stairs waiting: {Service} state {State} canTrigger {CanTrigger}",
                    targetId,
                    target.SemanticState,
                    target.CanTrigger);
                return;
            }

            _handledGateKey = gateKey;
            RecordDecision("jetway/stairs", $"connecting {targetId} at {gateKey}; {lvarDetail}");
            _lifecycle.MarkCalled(targetId);
            _ = TriggerAsync(targetId);
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

    private async Task TriggerAsync(string serviceId)
    {
        var result = await _api.SendCommandAsync(
            "service.trigger",
            new JsonObject { ["service"] = serviceId }).ConfigureAwait(false);
        if (!result.Ok)
        {
            _handledGateKey = null; // allow a retry on the next tick
            RecordDecision("jetway/stairs", $"{serviceId} trigger rejected ({result.Code})");
        }
    }

    private void RecordDecision(string action, string reason)
    {
        _logger.LogInformation("GSX {Action}: {Reason}", action, reason);
        _diagnostics.RecordDecision(new GsxDecisionView(DateTimeOffset.UtcNow, action, reason));
    }
}
