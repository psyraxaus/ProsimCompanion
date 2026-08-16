using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Gsx.Automation;
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
    private const string JetwayServiceId = GsxServiceIds.OperateJetways;
    private const string StairsServiceId = GsxServiceIds.OperateStairs;

    /// <summary>Connect confirm window — deliberately longer than the shared default: GSX has
    /// been observed taking 15 s+ to surface a jetway edge at busy airports, and the fallback
    /// (try the other service) must not fire while the first is still plausibly coming.</summary>
    private static readonly TimeSpan ConnectConfirmWindow = TimeSpan.FromSeconds(20);

    private readonly IGsxRemoteApi _api;
    private readonly IGsxTriggerSlot _slot;
    private readonly IOptionsMonitor<GsxOptions> _options;
    private readonly GsxDiagnosticsStore _diagnostics;
    private readonly ILogger<GsxJetwayStairsService> _logger;
    private readonly IDataRefSubscription _jetwayLvar;
    private readonly IDataRefSubscription _stairsLvar;
    private readonly IDataRefSubscription _operateJetwaysState;
    private readonly IDataRefSubscription _operateStairsState;
    private string? _handledGateKey;
    private string? _currentGateKey;
    private volatile string? _pendingServiceId;
    private bool _fallbackTried;
    private int _checking;

    /// <summary>L:FSDT_GSX_JETWAY value meaning "no jetway exists at this position" — live GSX 4
    /// session 2026-08-02: a remote stand read jetway=2 while the mirror still offered a
    /// (non-functional) OperateJetways service.</summary>
    private const int JetwayLvarNotPresent = 2;

    public GsxJetwayStairsService(
        IGsxRemoteApi api,
        IGsxTriggerSlot slot,
        ISimVars simVars,
        IOptionsMonitor<GsxOptions> options,
        GsxDiagnosticsStore diagnostics,
        ILogger<GsxJetwayStairsService> logger)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(simVars);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(logger);

        _api = api;
        _slot = slot;
        _options = options;
        _diagnostics = diagnostics;
        _logger = logger;

        _jetwayLvar = simVars.Subscribe(GsxLvarNames.Jetway, "number", DataRefTier.Normal);
        _stairsLvar = simVars.Subscribe(GsxLvarNames.Stairs, "number", DataRefTier.Normal);
        _operateJetwaysState = simVars.Subscribe(GsxLvarNames.OperateJetwaysState, "number", DataRefTier.Normal);
        _operateStairsState = simVars.Subscribe(GsxLvarNames.OperateStairsState, "number", DataRefTier.Normal);

        _api.Mirror.SidChanged += OnSidChanged;
    }

    public void Dispose()
    {
        _api.Mirror.SidChanged -= OnSidChanged;
        _jetwayLvar.Dispose();
        _stairsLvar.Dispose();
        _operateJetwaysState.Dispose();
        _operateStairsState.Dispose();
    }

    private void OnSidChanged(string? oldSid, string? newSid) => _handledGateKey = null;

    /// <summary>One coordinator-driven attempt — step 4 of ground prep (after GPU/chocks).
    /// Done when connected, disabled, given up, or nothing to connect; Pending while a trigger
    /// awaits its Requested/Active edge; Waiting otherwise.</summary>
    public GsxPrepStatus RunStep()
        => RunConnectStep(_options.CurrentValue.AutoConnectJetwayOrStairs);

    /// <summary>Departure-services-start connect (predecessor CallJetwayStairsDuringDeparture)
    /// — same once-per-gate core, so a jetway the prep step already connected is a no-op.</summary>
    public GsxPrepStatus RunDepartureStep()
        => RunConnectStep(_options.CurrentValue.CallJetwayStairsDuringDeparture);

    /// <summary>Arrival connect once stably parked (predecessor CallJetwayStairsOnArrival).
    /// The arrival gate has a fresh gate-context key, which resets the once-per-gate cycle.</summary>
    public GsxPrepStatus RunArrivalStep()
        => RunConnectStep(_options.CurrentValue.CallJetwayStairsOnArrival);

    private GsxPrepStatus RunConnectStep(bool optionEnabled)
    {
        if (Interlocked.Exchange(ref _checking, 1) == 1)
        {
            return GsxPrepStatus.Pending;
        }

        try
        {
            var gateKey = _api.Mirror.GateContextKey;
            if (!optionEnabled)
            {
                return GsxPrepStatus.Done;
            }

            if (gateKey is null)
            {
                return GsxPrepStatus.Waiting;
            }

            // Fresh gate context resets the whole cycle.
            if (!string.Equals(gateKey, _currentGateKey, StringComparison.Ordinal))
            {
                _currentGateKey = gateKey;
                _handledGateKey = null;
                _pendingServiceId = null;
                _fallbackTried = false;
            }

            // A dispatched connect resolves via the trigger slot's watcher (OnConnectResolved)
            // — this step just reports Pending until it does.
            if (_pendingServiceId is not null)
            {
                return GsxPrepStatus.Pending;
            }

            if (string.Equals(gateKey, _handledGateKey, StringComparison.Ordinal))
            {
                return GsxPrepStatus.Done;
            }

            var services = _api.Mirror.Services;
            if (services.Count == 0)
            {
                return GsxPrepStatus.Waiting; // mirror not populated yet — try again next cycle
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
                return GsxPrepStatus.Done;
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
                return GsxPrepStatus.Done;
            }

            if (target.State != GsxServiceState.Callable || !target.CanTrigger)
            {
                _logger.LogDebug(
                    "Jetway/stairs waiting: {Service} state {State} canTrigger {CanTrigger}",
                    targetId,
                    target.SemanticState,
                    target.CanTrigger);
                return GsxPrepStatus.Waiting;
            }

            RecordDecision(
                "jetway/stairs",
                $"connecting {targetId} at {gateKey}{(jetwayExists ? "" : " (jetway LVAR=2 — no jetway at this stand)")}; {lvarDetail}");
            StartTrigger(targetId);
            return GsxPrepStatus.Pending;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Jetway/stairs step failed");
            return GsxPrepStatus.Waiting;
        }
        finally
        {
            Interlocked.Exchange(ref _checking, 0);
        }
    }

    /// <summary>A trigger is only trusted once the trigger slot's watcher confirms the
    /// Requested/Active/Completed edge — GSX has been observed acking OperateJetways at a
    /// jetway-less stand and doing nothing. A drop OR a rejection falls back to the other
    /// service once (the old direct-send path burned 20 s waiting out a definitive rejection
    /// before falling back; the slot resolves it immediately).</summary>
    private void OnConnectResolved(string serviceId, string gateKey, GsxTriggerResolution resolution)
    {
        if (!string.Equals(_pendingServiceId, serviceId, StringComparison.OrdinalIgnoreCase))
        {
            return; // superseded by a gate-context reset
        }

        _pendingServiceId = null;
        if (resolution == GsxTriggerResolution.Confirmed)
        {
            _handledGateKey = gateKey;
            RecordDecision("jetway/stairs", $"{serviceId} responding");
            return;
        }

        var fallbackId = serviceId == JetwayServiceId ? StairsServiceId : JetwayServiceId;
        if (!_fallbackTried && _api.Mirror.Services.TryGetValue(fallbackId, out var fallback)
            && fallback.State == GsxServiceState.Callable && fallback.CanTrigger)
        {
            _fallbackTried = true;
            RecordDecision("jetway/stairs", $"{serviceId} showed no response — falling back to {fallbackId}");
            StartTrigger(fallbackId);
        }
        else
        {
            _handledGateKey = gateKey;
            RecordDecision("jetway/stairs", $"{serviceId} showed no response and no usable fallback — giving up for this gate");
        }
    }

    /// <summary>Removes the stairs after the departure services complete (predecessor
    /// RemoveStairsAfterDepature): "always", or "onlyJetway" when a jetway also reads
    /// connected (the pax doors stay served). "never"/unknown does nothing.</summary>
    public async Task RemoveStairsAfterDepartureAsync(string mode)
    {
        var stairs = _api.Mirror.Services.GetValueOrDefault(StairsServiceId);
        if (stairs?.State is not (GsxServiceState.Active or GsxServiceState.Completed))
        {
            return; // stairs not connected — nothing to remove
        }

        var jetwayConnected = _api.Mirror.Services.GetValueOrDefault(JetwayServiceId)?.State
            is GsxServiceState.Active or GsxServiceState.Completed;
        var due = mode.ToLowerInvariant() switch
        {
            "always" => true,
            "onlyjetway" => jetwayConnected,
            _ => false,
        };
        if (!due)
        {
            return;
        }

        RecordDecision(
            "jetway/stairs",
            $"departure services complete — removing stairs ({mode}{(jetwayConnected ? ", jetway stays" : "")})");
        await DispatchRemovalAsync(StairsServiceId).ConfigureAwait(false);
    }

    /// <summary>Retracts whatever is connected (the departure-sequence jetway step). The
    /// operate services are toggles, so this triggers only a service currently reading
    /// Active/Completed — never one that is retracted (that would CONNECT it).</summary>
    public async Task RequestRemovalAsync()
    {
        var removedAny = false;
        foreach (var serviceId in new[] { JetwayServiceId, StairsServiceId })
        {
            var service = _api.Mirror.Services.GetValueOrDefault(serviceId);
            if (service?.State is GsxServiceState.Active or GsxServiceState.Completed)
            {
                removedAny = true;
                RecordDecision("jetway/stairs", $"departure — retracting {serviceId}");
                await DispatchRemovalAsync(serviceId).ConfigureAwait(false);
            }
        }
        if (!removedAny)
        {
            RecordDecision("jetway/stairs", "departure — nothing connected to retract");
        }
    }

    /// <summary>Toggle retraction through the slot: NoConfirm (the confirming edge for a
    /// retraction is the service LEAVING Active/Completed — the connect predicate would lie),
    /// with the slot's spacing interval separating a jetway+stairs pair that used to go out
    /// back-to-back with zero gap. The long slot wait matters: this can be step 2 of the
    /// beacon pushback sequence, and skipping it risks pushing with the jetway attached.</summary>
    private async Task DispatchRemovalAsync(string serviceId)
    {
        var dispatch = await _slot
            .TryDispatchAsync(new GsxTriggerRequest(serviceId, "jetway/stairs removal")
            {
                NoConfirm = true,
                SlotWait = TimeSpan.FromSeconds(10),
            })
            .ConfigureAwait(false);
        if (dispatch.Status != GsxTriggerDispatchStatus.Dispatched)
        {
            RecordDecision(
                "jetway/stairs",
                dispatch.Status == GsxTriggerDispatchStatus.Busy
                    ? $"{serviceId} retraction NOT sent — trigger slot stayed busy with {dispatch.BusyServiceId ?? "another service"}"
                    : $"{serviceId} retraction rejected ({dispatch.RejectCode})");
        }
    }

    private void StartTrigger(string serviceId)
    {
        var gateKey = _currentGateKey ?? string.Empty;
        _pendingServiceId = serviceId;
        _ = DispatchConnectAsync(serviceId, gateKey);
    }

    private async Task DispatchConnectAsync(string serviceId, string gateKey)
    {
        try
        {
            var dispatch = await _slot
                .TryDispatchAsync(new GsxTriggerRequest(serviceId, "jetway/stairs connect")
                {
                    ConfirmWindow = ConnectConfirmWindow,
                    SlotWait = TimeSpan.FromSeconds(10),
                    OnResolved = resolution => OnConnectResolved(serviceId, gateKey, resolution),
                })
                .ConfigureAwait(false);
            if (dispatch.Status == GsxTriggerDispatchStatus.Busy
                && string.Equals(_pendingServiceId, serviceId, StringComparison.OrdinalIgnoreCase))
            {
                // Nothing went out — clear pending so the next coordinator cycle retries.
                _pendingServiceId = null;
                RecordDecision(
                    "jetway/stairs",
                    $"{serviceId} connect waiting — trigger slot busy with {dispatch.BusyServiceId ?? "another service"}");
            }
        }
        catch (Exception ex)
        {
            _pendingServiceId = null;
            _logger.LogError(ex, "Jetway/stairs connect dispatch for {Service} failed", serviceId);
        }
    }

    private void RecordDecision(string action, string reason)
    {
        _logger.LogInformation("GSX {Action}: {Reason}", action, reason);
        _diagnostics.RecordDecision(new GsxDecisionView(DateTimeOffset.UtcNow, action, reason));
    }
}
