using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Diagnostics.Ecam.HardwareCheck;
using ProsimCompanion.Core.Hardware.Skalarki;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Skalarki;

/// <summary>
/// Keeps the Profiler link up while the feature is on: no-op (and silent) while disabled or
/// while the SDK folder does not validate, otherwise connect with backoff, register the
/// watched boards' inputs, feed <see cref="SkalarkiStatusStore"/> from the client's events,
/// publish <see cref="Subsystems.Skalarki"/> with a reason, and start over on any settings
/// change. Every SDK call is wrapped: a failure sets the link state, never reaches the host.
/// The client is disposed with a timeout so a wedged SDK cannot hold up shutdown.
/// </summary>
public sealed class SkalarkiConnectionService : BackgroundService, ISkalarkiControl
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan TickPeriod = TimeSpan.FromSeconds(1);

    private readonly IOptionsMonitor<SkalarkiOptions> _options;
    private readonly ISkalarkiClientFactory _factory;
    private readonly SkalarkiStatusStore _store;
    private readonly ConnectionStatusStore _status;
    private readonly ILogger<SkalarkiConnectionService> _logger;
    private readonly TimeProvider _clock;
    private readonly SkalarkiStateCore _core = new();
    private readonly HardwareCheckSession _check = new();
    private readonly object _gate = new();
    private ISkalarkiClient? _client;
    private string? _guidedFor;

    public SkalarkiConnectionService(
        IOptionsMonitor<SkalarkiOptions> options,
        ISkalarkiClientFactory factory,
        SkalarkiStatusStore store,
        ConnectionStatusStore status,
        ILogger<SkalarkiConnectionService> logger,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _factory = factory;
        _store = store;
        _status = status;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>The state core, for tests that drive one session by hand.</summary>
    internal SkalarkiStateCore Core => _core;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var options = _options.CurrentValue;
                if (!options.Enabled)
                {
                    // Off: no page, no caution, no log line (verification step 3).
                    Publish(_core.Disabled(null));
                    _status.Set(Subsystems.Skalarki, ConnectionState.Disabled, "Skalarki panels are switched off.");
                    await WaitForOptionsChangeAsync(stoppingToken).ConfigureAwait(false);
                    continue;
                }

                var refusal = _factory.Validate(options.SdkPath);
                if (refusal is not null)
                {
                    Publish(_core.Disabled(refusal));
                    _status.Set(Subsystems.Skalarki, ConnectionState.Disabled, refusal);
                    if (!string.Equals(_guidedFor, options.SdkPath ?? "", StringComparison.Ordinal))
                    {
                        _guidedFor = options.SdkPath ?? "";
                        _logger.LogWarning("Skalarki link is off: {Reason} Set the SDK folder on the Skalarki settings page", refusal);
                    }

                    await WaitForOptionsChangeAsync(stoppingToken).ConfigureAwait(false);
                    continue;
                }

                await RunSessionAsync(options, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Skalarki link failed to start; continuing without it");
            Publish(_core.Disabled("The Skalarki link failed to start. See the Logs page."));
            _status.Set(Subsystems.Skalarki, ConnectionState.Disabled, "The Skalarki link failed to start. See the Logs page.");
        }
        finally
        {
            await DropClientAsync().ConfigureAwait(false);
        }
    }

    /// <summary>One client lifetime: until the settings change or the host stops.</summary>
    private async Task RunSessionAsync(SkalarkiOptions options, CancellationToken stoppingToken)
    {
        ISkalarkiClient client;
        try
        {
            client = _factory.Create(options.SdkPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var reason = "The Skalarki SDK bridge failed to load: " + ex.Message;
            _logger.LogWarning(ex, "Skalarki SDK bridge failed to load from {Path}", options.SdkPath);
            Publish(_core.Disabled(reason));
            _status.Set(Subsystems.Skalarki, ConnectionState.Disabled, reason);
            await WaitForOptionsChangeAsync(stoppingToken).ConfigureAwait(false);
            return;
        }

        lock (_gate)
        {
            _client = client;
        }

        using var optionsChanged = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using var subscription = _options.OnChange(_ => optionsChanged.Cancel());
        var token = optionsChanged.Token;
        var linkLost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        client.LinkChanged += up =>
        {
            if (up)
            {
                return;
            }

            linkLost.TrySetResult();
        };
        client.DeviceChanged += (device, connected) =>
        {
            var now = _clock.GetUtcNow();
            Publish(_core.DeviceChanged(device, connected, now));
            lock (_check)
            {
                if (connected ? _check.OnDeviceBack(device) : _check.OnDeviceLost(device, now))
                {
                    Publish(_core.SetCheck(_check.View));
                }
            }
        };
        client.HardwareEvent += hardwareEvent =>
        {
            Publish(_core.HardwareEvent(hardwareEvent));
            lock (_check)
            {
                if (_check.OnDeviceEvent(hardwareEvent.Device, hardwareEvent.Control, hardwareEvent.AtUtc))
                {
                    Publish(_core.SetCheck(_check.View));
                }
            }
        };

        Publish(_core.SetSdkVersion(Safe(() => client.SdkVersion)));
        Publish(_core.Configure(options.EnabledDeviceIds()));

        try
        {
            while (!token.IsCancellationRequested)
            {
                var endpoint = $"{options.ProfilerHost}:{options.ProfilerPort}";
                Publish(_core.Connecting());
                _status.Set(Subsystems.Skalarki, ConnectionState.Connecting, "Connecting to the Skalarki Profiler at " + endpoint + ".");
                try
                {
                    using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    connectTimeout.CancelAfter(ConnectTimeout);
                    await client.ConnectAsync(options.ProfilerHost, options.ProfilerPort, connectTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    var reason = $"The Skalarki Profiler at {endpoint} did not answer ({Short(ex)}).";
                    Publish(_core.LinkDown(_clock.GetUtcNow(), reason));
                    _status.Set(Subsystems.Skalarki, ConnectionState.Disconnected, reason);
                    var pause = _core.NextBackoff(options.ReconnectBackoff);
                    if (_core.Attempt == 1)
                    {
                        _logger.LogWarning("Skalarki Profiler at {Endpoint} did not answer: {Error}; retrying every {Pause}s", endpoint, Short(ex), pause.TotalSeconds);
                    }

                    await Task.Delay(pause, _clock, token).ConfigureAwait(false);
                    continue;
                }

                linkLost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Publish(_core.LinkUp(_clock.GetUtcNow()));
                _status.Set(Subsystems.Skalarki, ConnectionState.Connected, "Skalarki Profiler at " + endpoint + ".");
                _logger.LogInformation("Skalarki Profiler link up at {Endpoint} (SDK {Version})", endpoint, _core.Snapshot.SdkVersion);

                Publish(_core.SetLicense(await SafeAsync(() => client.GetLicenseAsync(token)).ConfigureAwait(false)));

                var watched = options.EnabledDeviceIds();
                try
                {
                    client.RegisterAll(watched.Count == 0 ? SkalarkiDevices.All : watched, options.Catalogue);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Skalarki RegisterEvents failed; presence still reports, inputs will not");
                }

                // Connected: tick the rate once a second until the link drops or settings change.
                using var tick = new PeriodicTimer(TickPeriod, _clock);
                while (!token.IsCancellationRequested && !linkLost.Task.IsCompleted)
                {
                    var next = tick.WaitForNextTickAsync(token).AsTask();
                    await Task.WhenAny(next, linkLost.Task).ConfigureAwait(false);
                    Publish(_core.Tick(_clock.GetUtcNow()));
                }

                if (token.IsCancellationRequested)
                {
                    break;
                }

                const string lost = "The Skalarki Profiler link was lost; reconnecting.";
                Publish(_core.LinkDown(_clock.GetUtcNow(), lost));
                _status.Set(Subsystems.Skalarki, ConnectionState.Disconnected, lost);
                _logger.LogWarning("Skalarki Profiler link lost; reconnecting");
                await Task.Delay(_core.NextBackoff(options.ReconnectBackoff), _clock, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Settings changed or the host is stopping: fall through and rebuild (or stop).
        }
        finally
        {
            await DropClientAsync().ConfigureAwait(false);
            if (!stoppingToken.IsCancellationRequested)
            {
                Publish(_core.LinkDown(_clock.GetUtcNow(), null));
            }
        }
    }

    // ---- ISkalarkiControl: the PANELS page's three buttons (package 2) ----

    public async Task<string> LampTestAsync(SkalarkiDeviceId? device, CancellationToken cancellationToken)
    {
        var client = CurrentClient();
        if (client is null || _core.Snapshot.LinkState != ConnectionState.Connected)
        {
            return "NO LINK";
        }

        var targets = device is { } one
            ? [one]
            : _core.Snapshot.Devices.Where(p => p.Value is { Enabled: true, Connected: true }).Select(p => p.Key).ToList();
        if (targets.Count == 0)
        {
            return "NO DEVICE";
        }

        var hold = TimeSpan.FromMilliseconds(Math.Clamp(_options.CurrentValue.LampTestMs, 500, 10_000));
        var lit = 0;
        foreach (var target in targets)
        {
            if (Safe(() => { client.SetDeviceOutputs(target, true); return true; }))
            {
                lit++;
            }
        }

        _logger.LogInformation("Skalarki LAMP TEST: {Lit} of {Count} boards lit for {Hold}ms", lit, targets.Count, hold.TotalMilliseconds);
        try
        {
            await Task.Delay(hold, _clock, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Off again whatever happened — a cancelled test must not leave a panel lit.
            foreach (var target in targets)
            {
                Safe(() => { client.SetDeviceOutputs(target, false); return true; });
            }
        }

        return lit == 0 ? "FAILED" : $"{lit} LIT";
    }

    public Task<string> HardwareResetAsync(SkalarkiDeviceId device, bool hard, CancellationToken cancellationToken)
    {
        var client = CurrentClient();
        if (client is null || _core.Snapshot.LinkState != ConnectionState.Connected)
        {
            return Task.FromResult("NO LINK");
        }

        var ok = Safe(() => { client.HardwareReset(device, hard); return true; });
        _logger.LogWarning("Skalarki HW RESET sent to {Device} (hard: {Hard}) from the diagnostics page: {Outcome}", device, hard, ok ? "sent" : "failed");
        return Task.FromResult(ok ? "SENT" : "FAILED");
    }

    public void StartCheck(IReadOnlyDictionary<SkalarkiDeviceId, string> prompts)
    {
        ArgumentNullException.ThrowIfNull(prompts);
        var snapshot = _core.Snapshot;
        var devices = snapshot.EnabledDevices.Select(p => p.Key).ToList();
        lock (_check)
        {
            _check.Start(devices, prompts, snapshot.Devices, _clock.GetUtcNow());
            Publish(_core.SetCheck(_check.View));
        }
    }

    public void RestartCheck()
    {
        lock (_check)
        {
            if (_check.View is null)
            {
                return;
            }

            _check.Restart(_core.Snapshot.Devices, _clock.GetUtcNow());
            Publish(_core.SetCheck(_check.View));
        }
    }

    public void StopCheck()
    {
        lock (_check)
        {
            _check.Stop();
            Publish(_core.SetCheck(null));
        }
    }

    private ISkalarkiClient? CurrentClient()
    {
        lock (_gate)
        {
            return _client;
        }
    }

    private void Publish(SkalarkiSnapshot snapshot) => _store.Publish(snapshot);

    private async Task WaitForOptionsChangeAsync(CancellationToken stoppingToken)
    {
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = _options.OnChange(_ => changed.TrySetResult());
        await changed.Task.WaitAsync(stoppingToken).ConfigureAwait(false);
    }

    /// <summary>Disconnects and disposes the current client off the caller's thread, giving the
    /// SDK a bounded time: a wedged Dataflow block must not hold up shutdown.</summary>
    private async Task DropClientAsync()
    {
        ISkalarkiClient? client;
        lock (_gate)
        {
            client = _client;
            _client = null;
        }

        if (client is null)
        {
            return;
        }

        var dispose = Task.Run(async () =>
        {
            try
            {
                await client.DisconnectAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Skalarki client disconnect threw; disposing anyway");
            }

            client.Dispose();
        });
        var finished = await Task.WhenAny(dispose, Task.Delay(DisposeTimeout)).ConfigureAwait(false);
        if (finished != dispose)
        {
            _logger.LogWarning("Skalarki SDK client did not dispose within {Seconds}s; abandoned", DisposeTimeout.TotalSeconds);
        }
        else if (dispose.IsFaulted)
        {
            _logger.LogDebug(dispose.Exception, "Skalarki client dispose threw");
        }
    }

    private T? Safe<T>(Func<T> call)
    {
        try
        {
            return call();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Skalarki SDK call failed");
            return default;
        }
    }

    private async Task<T?> SafeAsync<T>(Func<Task<T?>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Skalarki SDK call failed");
            return default;
        }
    }

    private static string Short(Exception ex) => ex.InnerException?.Message ?? ex.Message;
}
