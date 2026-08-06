using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Aircraft.Gateway;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Audio;

/// <summary>
/// Clears ProSim's native per-window audio bindings (aircraft.communication.windows.*) once per
/// ProSim connection while this application owns the volumes — never let both control the same
/// sessions. Mirrors the GSX native-flags guard. PA is deliberately NOT cleared: the
/// predecessor never touched it (archaeology, Prosim2GSX DisableNativeIntegration) and the
/// rewrite preserves that until live testing proves otherwise.
/// </summary>
public sealed class ProsimNativeAudioGuard : IDisposable
{
    private static readonly string[] NativeAudioWindows =
    [
        "vhf1", "vhf2", "vhf3", "hf1", "hf2", "int", "cab",
    ];

    private readonly IProsimGateway _gateway;
    private readonly ConnectionStatusStore _status;
    private readonly IOptionsMonitor<AudioOptions> _options;
    private readonly ILogger<ProsimNativeAudioGuard> _logger;
    private bool _appliedThisConnection;

    public ProsimNativeAudioGuard(
        IProsimGateway gateway,
        ConnectionStatusStore status,
        IOptionsMonitor<AudioOptions> options,
        ILogger<ProsimNativeAudioGuard> logger)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _gateway = gateway;
        _status = status;
        _options = options;
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
            // Re-apply on the next connection (a ProSim restart restores its own bindings).
            _appliedThisConnection = false;
            return;
        }

        if (_appliedThisConnection
            || !_options.CurrentValue.Enabled
            || !_options.CurrentValue.DisableProsimNativeAudio)
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
            // Let the connection settle before the first writes (same grace as the GSX guard).
            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

            var failed = new List<string>();
            foreach (var window in NativeAudioWindows)
            {
                var name = ProsimDataRefNames.AudioWindowsPrefix + window;
                if (!await _gateway.WriteDataRefAsync(name, "").ConfigureAwait(false))
                {
                    failed.Add(window);
                }
            }

            if (failed.Count == 0)
            {
                _logger.LogInformation(
                    "Cleared {Count} ProSim native audio window bindings", NativeAudioWindows.Length);
            }
            else
            {
                _appliedThisConnection = false; // retry on the next status change
                _logger.LogWarning(
                    "Clearing ProSim native audio bindings failed for: {Windows} — will retry",
                    string.Join(", ", failed));
            }
        }
        catch (Exception ex)
        {
            _appliedThisConnection = false;
            _logger.LogError(ex, "Clearing ProSim native audio bindings failed");
        }
    }
}
