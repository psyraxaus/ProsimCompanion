using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.State;
using ProsimCompanion.Gsx.Protocol;
using ProsimCompanion.Gsx.Services;

namespace ProsimCompanion.Gsx.Automation;

/// <summary>
/// The trigger slot (see <see cref="IGsxTriggerSlot"/>): one <c>service.trigger</c> in flight
/// at a time, confirmed against the state mirror / lifecycle edge — never the command ack
/// (round-7 smoke test: five simultaneous triggers, only the last service ran while the board
/// showed the rest "Called" forever). The service cycle is marked called only on confirmation,
/// so a call GSX never accepted is never shown as pending. Two consecutive silent drops of the
/// same service raise the open-menu advisory (issue #44) regardless of which sender dropped.
/// </summary>
public sealed class GsxTriggerSlot : IGsxTriggerSlot, IDisposable
{
    /// <summary>Cadence of the confirm poll and of the slot-wait retry loop.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>How long a <see cref="GsxTriggerRequest.NoConfirm"/> send keeps the slot
    /// occupied. GSX drops the second of two back-to-back triggers, and the jetway/stairs
    /// removal pair used to go out with zero spacing — this interval is the fix.</summary>
    private static readonly TimeSpan NoConfirmSpacing = TimeSpan.FromSeconds(2);

    private sealed record InFlightTrigger(string ServiceId, DateTimeOffset SentAt);

    private readonly IGsxRemoteApi _api;
    private readonly GsxServiceLifecycleTracker _lifecycle;
    private readonly IOptionsMonitor<GsxOptions> _options;
    private readonly GsxDiagnosticsStore _diagnostics;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<GsxTriggerSlot> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile InFlightTrigger? _inFlight;
    private string? _lastDroppedService;
    private int _consecutiveDrops;

    public GsxTriggerSlot(
        IGsxRemoteApi api,
        GsxServiceLifecycleTracker lifecycle,
        IOptionsMonitor<GsxOptions> options,
        GsxDiagnosticsStore diagnostics,
        JsonlEventLog eventLog,
        ILogger<GsxTriggerSlot> logger)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _api = api;
        _lifecycle = lifecycle;
        _options = options;
        _diagnostics = diagnostics;
        _eventLog = eventLog;
        _logger = logger;
    }

    /// <inheritdoc />
    public string? InFlightServiceId => _inFlight?.ServiceId;

    /// <inheritdoc />
    public event Action? Changed;

    /// <inheritdoc />
    public void Reset(string reason)
    {
        if (_inFlight is not { } inFlight)
        {
            return;
        }

        _inFlight = null;
        RecordDecision($"trigger {inFlight.ServiceId}", $"cleared without resolution — {reason}");
        Changed?.Invoke();
    }

    public void Dispose() => _gate.Dispose();

    /// <inheritdoc />
    public async Task<GsxTriggerDispatch> TryDispatchAsync(
        GsxTriggerRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ServiceId);

        var deadline = DateTimeOffset.UtcNow + request.SlotWait;
        while (true)
        {
            string busyServiceId;
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_inFlight is not { } occupied)
                {
                    _inFlight = new InFlightTrigger(request.ServiceId, DateTimeOffset.UtcNow);
                    break;
                }
                busyServiceId = occupied.ServiceId;
            }
            finally
            {
                _gate.Release();
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return new(GsxTriggerDispatchStatus.Busy, busyServiceId);
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }

        RecordDecision($"trigger {request.ServiceId}", $"requested by {request.Source}");
        var result = await SendTriggerAsync(request.ServiceId, cancellationToken).ConfigureAwait(false);
        if (!result.Ok)
        {
            ClearIfOwned(request.ServiceId);
            RecordDecision($"trigger {request.ServiceId}", $"rejected ({result.Code})");
            SafeResolve(request, GsxTriggerResolution.Rejected);
            Changed?.Invoke();
            return new(GsxTriggerDispatchStatus.Rejected, RejectCode: result.Code);
        }

        if (request.NoConfirm)
        {
            _ = ReleaseAfterSpacingAsync(request.ServiceId);
        }
        else
        {
            _ = WatchAsync(request, isRetry: false);
        }

        return new(GsxTriggerDispatchStatus.Dispatched);
    }

    /// <summary>The only place in the codebase that builds the <c>service.trigger</c> payload
    /// — see the invariant on <see cref="IGsxTriggerSlot"/>.</summary>
    private Task<GsxCommandResult> SendTriggerAsync(string serviceId, CancellationToken cancellationToken = default)
        => _api.SendCommandAsync(
            "service.trigger",
            new JsonObject { ["service"] = serviceId },
            cancellationToken);

    /// <summary>Holds the slot for the spacing interval after an unwatched (toggle) send, then
    /// frees it. The toggle's real effect shows up as mirror state on its own.</summary>
    private async Task ReleaseAfterSpacingAsync(string serviceId)
    {
        try
        {
            await Task.Delay(NoConfirmSpacing).ConfigureAwait(false);
        }
        finally
        {
            ClearIfOwned(serviceId);
            Changed?.Invoke();
        }
    }

    /// <summary>Confirm-or-timeout watcher — the single implementation all senders share.
    /// Confirmed = lifecycle cycle or mirror state reaches Requested/Active/Completed; only
    /// then is the cycle marked called and the next dispatch allowed out. A first-attempt
    /// timeout re-sends once when the request asks for it (issue #76: the sequencer re-offers
    /// dropped calls itself, but an on-demand GPU call at arrival simply died).</summary>
    private async Task WatchAsync(GsxTriggerRequest request, bool isRetry)
    {
        var serviceId = request.ServiceId;
        try
        {
            var window = request.ConfirmWindow
                ?? TimeSpan.FromMilliseconds(_options.CurrentValue.TriggerConfirmTimeoutMs);
            var deadline = DateTimeOffset.UtcNow + window;
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (!OwnsSlot(serviceId))
                {
                    return; // reset or superseded — nothing left to own
                }

                if (IsConfirmed(serviceId, out var confirmedBy))
                {
                    _lifecycle.MarkCalled(serviceId);
                    ClearIfOwned(serviceId);
                    _lastDroppedService = null;
                    _consecutiveDrops = 0;
                    _diagnostics.UpdateDroppedCall(null); // a confirmed call retires the warning
                    RecordDecision($"trigger {serviceId}", $"confirmed by GSX ({confirmedBy})");
                    SafeResolve(request, GsxTriggerResolution.Confirmed);
                    Changed?.Invoke();
                    return;
                }

                await Task.Delay(PollInterval).ConfigureAwait(false);
            }

            if (!OwnsSlot(serviceId))
            {
                return;
            }

            if (request.RetryOnce && !isRetry)
            {
                // Keep owning the slot with a fresh timestamp and fire the trigger once more.
                _inFlight = new InFlightTrigger(serviceId, DateTimeOffset.UtcNow);
                RecordDecision(
                    $"trigger {serviceId}",
                    "not picked up by GSX within the confirm window — retrying once automatically");
                var retry = await SendTriggerAsync(serviceId).ConfigureAwait(false);
                if (!retry.Ok)
                {
                    ClearIfOwned(serviceId);
                    RecordDecision($"trigger {serviceId}", $"automatic retry rejected ({retry.Code}) — the slot is free again");
                    SafeResolve(request, GsxTriggerResolution.Rejected);
                    Changed?.Invoke();
                    return;
                }

                await WatchAsync(request, isRetry: true).ConfigureAwait(false);
                return;
            }

            ClearIfOwned(serviceId);
            RecordDrop(serviceId, window);
            SafeResolve(request, GsxTriggerResolution.Dropped);
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Trigger watcher for {Service} failed", serviceId);
        }
    }

    private bool OwnsSlot(string serviceId)
        => _inFlight?.ServiceId.Equals(serviceId, StringComparison.OrdinalIgnoreCase) == true;

    private void ClearIfOwned(string serviceId)
    {
        if (OwnsSlot(serviceId))
        {
            _inFlight = null;
        }
    }

    private bool IsConfirmed(string serviceId, out string confirmedBy)
    {
        var cycles = _lifecycle.SnapshotCycles();
        cycles.TryGetValue(serviceId, out var cycle);
        var mirrorState = _api.Mirror.Services.TryGetValue(serviceId, out var info)
            ? info.State
            : (GsxServiceState?)null;
        if (mirrorState is GsxServiceState.Requested or GsxServiceState.Active or GsxServiceState.Completed)
        {
            confirmedBy = mirrorState.ToString()!;
            return true;
        }
        if (cycle.Requested || cycle.Active || cycle.Completed)
        {
            confirmedBy = "lifecycle edge";
            return true;
        }
        confirmedBy = string.Empty;
        return false;
    }

    /// <summary>Drop bookkeeping shared across ALL senders: two consecutive drops of the same
    /// service means GSX is refusing calls, not missing them. An open GSX menu at that moment
    /// is the usual culprit (issue #44: a facility/stand conflict kept "Change parking or
    /// service" up and every trigger died) — say so once per streak.</summary>
    private void RecordDrop(string serviceId, TimeSpan window)
    {
        _consecutiveDrops = string.Equals(_lastDroppedService, serviceId, StringComparison.OrdinalIgnoreCase)
            ? _consecutiveDrops + 1
            : 1;
        _lastDroppedService = serviceId;
        RecordDecision(
            $"trigger {serviceId}",
            $"not picked up by GSX within {(int)window.TotalSeconds} s — the call was dropped");

        // Surface it (issue #76): a log line is not a notification — the 2026-08-29
        // Deboarding call died at 15:31:33 with the pilot none the wiser. The diagnostics
        // view drives the Flight Status row and the FO's spoken advisory.
        var openMenu = _api.Mirror.MenuShown ? _api.Mirror.Menu?.Title : null;
        _diagnostics.UpdateDroppedCall(new GsxDroppedCallView(DateTimeOffset.UtcNow, serviceId, openMenu));
        _eventLog.Record("gsx-call-dropped", new { service = serviceId, openMenu, consecutive = _consecutiveDrops });

        if (_consecutiveDrops == 2)
        {
            RecordDecision(
                $"trigger {serviceId}",
                openMenu is null
                    ? "dropped twice in a row — GSX is not accepting service calls; check the GSX menu/state in the sim"
                    : $"dropped twice in a row while the GSX menu '{openMenu}' is open — resolve that menu; "
                        + "triggers are refused until it closes (see issue #44)");
        }
    }

    private void SafeResolve(GsxTriggerRequest request, GsxTriggerResolution resolution)
    {
        try
        {
            request.OnResolved?.Invoke(resolution);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OnResolved callback for {Service} failed", request.ServiceId);
        }
    }

    private void RecordDecision(string action, string reason)
    {
        _logger.LogInformation("GSX trigger slot: {Action} — {Reason}", action, reason);
        _diagnostics.RecordDecision(new GsxDecisionView(DateTimeOffset.UtcNow, action, reason));
        _eventLog.Record("gsx-decision", new { action, reason });
    }
}
