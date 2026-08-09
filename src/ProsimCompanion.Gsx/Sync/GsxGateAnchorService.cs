using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;
using ProsimCompanion.Gsx.Gate;

namespace ProsimCompanion.Gsx.Sync;

/// <summary>
/// Re-anchors GSX's remembered parking to the stand the aircraft actually occupies, once per
/// gate session during ground preparation (issue #44). GSX persists its assigned facility per
/// airport across sim sessions: a new flight spawned at a different stand leaves GSX split
/// between the old gate and the aircraft's position — service triggers then ack "ok" but die
/// inside GSX, surfacing only as unhandled "Change parking or service" menus. A
/// <c>gate.select</c> for the current gate collapses the split before any trigger is sent;
/// at prep time no services are active, so <c>revokeServices:false</c> is always safe.
/// </summary>
public sealed class GsxGateAnchorService : IDisposable
{
    private readonly IGsxRemoteApi _api;
    private readonly IOptionsMonitor<GsxOptions> _options;
    private readonly GsxDiagnosticsStore _diagnostics;
    private readonly ILogger<GsxGateAnchorService> _logger;
    private string? _handledGateKey;
    private int _running;

    public GsxGateAnchorService(
        IGsxRemoteApi api,
        IOptionsMonitor<GsxOptions> options,
        GsxDiagnosticsStore diagnostics,
        ILogger<GsxGateAnchorService> logger)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(logger);

        _api = api;
        _options = options;
        _diagnostics = diagnostics;
        _logger = logger;

        _api.Mirror.SidChanged += OnSidChanged;
    }

    public void Dispose() => _api.Mirror.SidChanged -= OnSidChanged;

    private void OnSidChanged(string? oldSid, string? newSid) => _handledGateKey = null;

    /// <summary>One coordinator-driven attempt (after the reposition settles, before ground
    /// equipment). Never blocks the prep chain: an unknown parking or a failed select logs a
    /// decision and reports Done.</summary>
    public async Task<GsxPrepStatus> RunStepAsync()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return GsxPrepStatus.Pending;
        }

        try
        {
            var gateKey = _api.Mirror.GateContextKey;
            if (!_options.CurrentValue.AnchorDepartureGate
                || string.Equals(gateKey, _handledGateKey, StringComparison.Ordinal))
            {
                return GsxPrepStatus.Done;
            }

            if (gateKey is null)
            {
                return GsxPrepStatus.Waiting;
            }

            // Latch first — a failed anchor must not loop gate.select every cycle.
            _handledGateKey = gateKey;

            var token = GsxGateResolver.ResolveAnchorToken(_api.Mirror.Parkings, gateKey);
            if (token is null)
            {
                RecordDecision($"skipped — the mirror has no parking matching '{gateKey}'");
                return GsxPrepStatus.Done;
            }

            var result = await _api.SendCommandAsync("gate.select", new JsonObject
            {
                ["gate"] = token,
                ["revokeServices"] = false,
                ["force"] = false,
            }).ConfigureAwait(false);

            RecordDecision(result.Ok || result.Code is "already_selected" or "already_parked" or "prepared"
                ? $"anchored GSX to '{token}' ({result.Code})"
                : $"gate.select '{token}' failed ({result.Code}) — GSX may still be on a previous session's gate");
            return GsxPrepStatus.Done;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Gate anchor step failed");
            return GsxPrepStatus.Done; // never block the rest of the prep chain
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private void RecordDecision(string reason)
    {
        _logger.LogInformation("GSX {Action}: {Reason}", "gate anchor", reason);
        _diagnostics.RecordDecision(new GsxDecisionView(DateTimeOffset.UtcNow, "gate anchor", reason));
    }
}
