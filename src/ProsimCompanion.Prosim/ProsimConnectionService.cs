using System.Runtime.CompilerServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;
using ProsimCompanion.Prosim.DataRefs;
using ProsimCompanion.Prosim.Sdk;

namespace ProsimCompanion.Prosim;

/// <summary>
/// Hosted service orchestrating the ProSim SDK lifecycle. Degrade-not-fail: when no SDK path is
/// configured (fresh install, installer skipped) the subsystem stays disabled with guidance and
/// watches settings so configuring the path in the web UI brings it up without a restart.
/// </summary>
public sealed class ProsimConnectionService : BackgroundService
{
    private readonly IOptionsMonitor<ProsimOptions> _options;
    private readonly ProsimDataRefService _dataRefs;
    private readonly ConnectionStatusStore _status;
    private readonly ILogger<ProsimConnectionService> _logger;
    private Sdk.SdkConnection? _connection;

    public ProsimConnectionService(
        IOptionsMonitor<ProsimOptions> options,
        ProsimDataRefService dataRefs,
        ConnectionStatusStore status,
        ILogger<ProsimConnectionService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(dataRefs);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _dataRefs = dataRefs;
        _status = status;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var sdkDirectory = await WaitForSdkDirectoryAsync(stoppingToken).ConfigureAwait(false);

            SdkAssemblyResolver.Register(sdkDirectory);
            await RunSessionsAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ProSim subsystem failed to start; continuing without it");
            _status.Set(Subsystems.Prosim, ConnectionState.Disabled);
        }
        finally
        {
            _connection?.Dispose();
            _connection = null;
            _status.Set(Subsystems.Prosim, ConnectionState.Disconnected);
        }
    }

    /// <summary>
    /// Resolves the SDK directory from settings, waiting for settings changes while it is
    /// missing or invalid.
    /// </summary>
    private async Task<string> WaitForSdkDirectoryAsync(CancellationToken stoppingToken)
    {
        var loggedGuidance = false;

        while (true)
        {
            stoppingToken.ThrowIfCancellationRequested();

            var configured = _options.CurrentValue.SdkPath;
            var directory = SdkLocator.Locate(configured);
            if (directory is not null)
            {
                return directory;
            }

            if (!loggedGuidance)
            {
                loggedGuidance = true;
                _status.Set(Subsystems.Prosim, ConnectionState.Disabled);
                if (string.IsNullOrWhiteSpace(configured))
                {
                    _logger.LogWarning(
                        "No ProSim SDK path is configured. Set it on the web Settings page (or re-run " +
                        "the installer); the ProSim subsystem stays disabled until then");
                }
                else
                {
                    _logger.LogWarning(
                        "ProSimSDK.dll was not found at the configured path {Path}. Correct it on the " +
                        "web Settings page; the ProSim subsystem stays disabled until then",
                        configured);
                }
            }

            await WaitForOptionsChangeAsync(stoppingToken).ConfigureAwait(false);
            loggedGuidance = false;
        }
    }

    private async Task WaitForOptionsChangeAsync(CancellationToken stoppingToken)
    {
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = _options.OnChange(_ => changed.TrySetResult());
        await changed.Task.WaitAsync(stoppingToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs SDK sessions until shutdown, rebuilding on a wedge. The SDK forbids stacking Connect
    /// calls on a live <c>ProSimConnect</c>, so recovery from a wedged session (a registration
    /// round-trip deadlocked against the SDK's receive thread — issue #35) is dispose-and-rebuild,
    /// never reconnect-in-place. Kept non-inlined so the JIT never touches SDK-typed code (and
    /// thus never resolves ProSimSDK.dll) before the assembly resolver is registered.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task RunSessionsAsync(CancellationToken stoppingToken)
    {
        while (true)
        {
            var connection = new SdkConnection(
                _options.CurrentValue,
                _dataRefs,
                _status,
                _logger);
            _connection = connection;
            connection.Start();

            await connection.WedgedTask.WaitAsync(stoppingToken).ConfigureAwait(false);

            _logger.LogWarning("Rebuilding the ProSim SDK session after a wedge");
            _connection = null;
            connection.Dispose();
            await Task.Delay(
                TimeSpan.FromMilliseconds(_options.CurrentValue.ReconnectIntervalMs),
                stoppingToken).ConfigureAwait(false);
        }
    }
}
