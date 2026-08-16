using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Aircraft.Gateway;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Gsx.Sync;

/// <summary>
/// Turns off ProSim's own GSX auto-integration flags (efb.gsx.*) once per ProSim connection
/// while this application drives GSX — otherwise two automations fight over the same services.
/// ProSim's auto-jetway/auto-door remain untouched for now (doors keep working natively until
/// our door automation lands). Decision-logged so the smoke test shows exactly what was turned
/// off.
/// </summary>
public sealed class ProsimNativeGsxGuard : IDisposable
{
    private static readonly string[] NativeGsxFlags =
    [
        ProsimDataRefNames.GsxAutoCatering,
        ProsimDataRefNames.GsxAutoPushback,
        ProsimDataRefNames.GsxAutoDisconnectGpu,
        ProsimDataRefNames.GsxAutoConnectGpu,
        ProsimDataRefNames.GsxAutoDeboard,
        ProsimDataRefNames.GsxAutoSelectOperator,
    ];

    private readonly IProsimGateway _gateway;
    private readonly ConnectionStatusStore _status;
    private readonly IOptionsMonitor<GsxOptions> _options;
    private readonly GsxDiagnosticsStore _diagnostics;
    private readonly ILogger<ProsimNativeGsxGuard> _logger;
    private bool _appliedThisConnection;

    public ProsimNativeGsxGuard(
        IProsimGateway gateway,
        ConnectionStatusStore status,
        IOptionsMonitor<GsxOptions> options,
        GsxDiagnosticsStore diagnostics,
        ILogger<ProsimNativeGsxGuard> logger)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(logger);

        _gateway = gateway;
        _status = status;
        _options = options;
        _diagnostics = diagnostics;
        _logger = logger;

        _status.Changed += OnStatusChanged;
    }

    public void Dispose() => _status.Changed -= OnStatusChanged;

    private void OnStatusChanged(object? sender, EventArgs e)
    {
        var prosimState = _status.Snapshot()
            .FirstOrDefault(pair => pair.Key == Subsystems.Prosim).Value;

        if (prosimState != ConnectionState.Connected)
        {
            // Re-apply on the next connection (ProSim restart resets its own settings).
            _appliedThisConnection = false;
            return;
        }

        if (_appliedThisConnection
            || !_options.CurrentValue.AutomationEnabled
            || !_options.CurrentValue.DisableProsimNativeGsx)
        {
            return;
        }

        _appliedThisConnection = true;
        _ = ApplyAsync();
    }

    private async Task ApplyAsync()
    {
        try
        {
            // Hold until the EFB gateway is actually listening (issue #76 item 6,
            // 2026-08-15 flight): ProSim raises the SDK connection before its port-5000
            // gateway starts, so writes fired at "SDK connected" burned their retry attempts
            // on "actively refused" (efb.gsx.autoCatering attempt 1-2/3). Bounded — after
            // the wait the writes go out regardless, with their retries as the backstop.
            // The flags are written via the gateway (writeBool) — the predecessors' proven
            // path for these; some are not writable as SDK datarefs.
            await WaitForGatewayAsync().ConfigureAwait(false);

            var failed = new List<string>();
            foreach (var flag in NativeGsxFlags)
            {
                if (!await _gateway.WriteDataRefAsync(flag, false).ConfigureAwait(false))
                {
                    failed.Add(flag);
                }
            }

            if (failed.Count == 0)
            {
                RecordDecision("native GSX guard", $"disabled {NativeGsxFlags.Length} ProSim efb.gsx.* auto flags");
            }
            else
            {
                _appliedThisConnection = false; // retry on the next status change
                RecordDecision("native GSX guard", $"failed for: {string.Join(", ", failed)} — will retry");
            }
        }
        catch (Exception ex)
        {
            _appliedThisConnection = false;
            _logger.LogError(ex, "Disabling ProSim native GSX flags failed");
        }
    }

    /// <summary>Probes the gateway every 2 s for up to a minute. Quiet (Debug-level probes)
    /// by design — the old path logged a warning per burned retry attempt.</summary>
    private async Task WaitForGatewayAsync()
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            if (await _gateway.IsReachableAsync().ConfigureAwait(false))
            {
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        _logger.LogDebug("ProSim gateway still unreachable after 60 s — writing anyway (retries are the backstop)");
    }

    private void RecordDecision(string action, string reason)
    {
        _logger.LogInformation("GSX {Action}: {Reason}", action, reason);
        _diagnostics.RecordDecision(new GsxDecisionView(DateTimeOffset.UtcNow, action, reason));
    }
}
