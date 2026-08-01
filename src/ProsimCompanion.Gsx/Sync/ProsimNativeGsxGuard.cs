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
            // Give the connection a moment to settle before the first writes. The flags are
            // written via the gateway (writeBool) — the predecessors' proven path for these;
            // some are not writable as SDK datarefs.
            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

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

    private void RecordDecision(string action, string reason)
    {
        _logger.LogInformation("GSX {Action}: {Reason}", action, reason);
        _diagnostics.RecordDecision(new GsxDecisionView(DateTimeOffset.UtcNow, action, reason));
    }
}
