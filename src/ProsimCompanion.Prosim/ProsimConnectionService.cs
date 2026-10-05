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
        var failedToStart = false;
        string? sdkDirectory = null;
        try
        {
            sdkDirectory = await WaitForSdkDirectoryAsync(stoppingToken).ConfigureAwait(false);

            SdkAssemblyResolver.Register(sdkDirectory);
            await RunSessionsAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex) when (SdkIncompatibleException.IsMismatch(ex))
        {
            // Issue #158: a member this app binds is not in the user's ProSimSDK.dll. The CLR
            // keeps the loaded dll for the life of the process, so the only way out is a
            // matching dll and a restart — say so instead of "failed to start".
            failedToStart = true;
            _logger.LogWarning(
                ex,
                "The ProSimSDK.dll in {Directory} does not match this version of the app ({Detail}). " +
                "The ProSim subsystem is off. Update ProSim, or set the ProSim SDK folder on the web " +
                "Settings page to a matching installation, then restart the app",
                sdkDirectory,
                ex.Message);
            _status.Set(
                Subsystems.Prosim,
                ConnectionState.Disabled,
                "The ProSim SDK in the configured folder does not match this version of the app. " +
                "Update ProSim or the app, then restart the app.");
        }
        catch (Exception ex)
        {
            failedToStart = true;
            _logger.LogError(ex, "ProSim subsystem failed to start; continuing without it");
            _status.Set(
                Subsystems.Prosim,
                ConnectionState.Disabled,
                "The ProSim connection failed to start. See the Logs page.");
        }
        finally
        {
            _connection?.Dispose();
            _connection = null;

            // A failed start stays Disabled with its reason: this line used to overwrite it
            // with Disconnected ("will keep retrying"), which nothing was doing (issue #158).
            if (!failedToStart)
            {
                _status.Set(Subsystems.Prosim, ConnectionState.Disconnected);
            }
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
                _status.Set(
                    Subsystems.Prosim,
                    ConnectionState.Disabled,
                    string.IsNullOrWhiteSpace(configured)
                        ? "No ProSim SDK folder is set."
                        : "ProSimSDK.dll was not found in the configured ProSim SDK folder.");
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
        // The dll file version is 1.1.1.0 on every ProSim build seen so far; the product
        // version (ProSim commit hash) and the constructor shape are what tell them apart
        // (issue #158, ticket t-20261005-1918).
        var sdk = SdkConnection.DescribeSdk();
        _logger.LogInformation(
            "ProSim SDK {Version} loaded from {Path} ({Shape} constructor)",
            sdk.ProductVersion,
            sdk.Location,
            SdkConstructorSelector.Describe(sdk.Shape));

        if (!string.IsNullOrWhiteSpace(_options.CurrentValue.ApiKey)
            && !sdk.Shape.HasFlag(SdkConstructorShape.ApiKey))
        {
            _logger.LogWarning(
                "A ProSim API key is set, but this ProSim SDK build has no API-key constructor; " +
                "the key is not used");
        }

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
