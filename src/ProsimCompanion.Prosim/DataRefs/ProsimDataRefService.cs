using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Prosim.DataRefs;

/// <summary>
/// The application-wide <see cref="IProsimDataRefs"/> implementation. Owns the subscription
/// table, the serialized momentary-press worker, and the push dispatcher that decouples
/// subscriber callbacks from the SDK's receive thread; a connected SDK session attaches itself
/// as the <see cref="IDataRefBackend"/>. With no backend attached, cached (stale-flagged) values
/// remain readable and writes fail fast with a clear error.
/// </summary>
public sealed class ProsimDataRefService : IProsimDataRefs, IAsyncDisposable
{
    private readonly DataRefSubscriptionTable _table;
    private readonly ILogger<ProsimDataRefService> _logger;
    private readonly IOptionsMonitor<ProsimOptions> _options;
    private readonly Channel<PressRequest> _presses;
    private readonly Channel<PushItem> _pushes;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _pressWorker;
    private readonly Task _pushDispatcher;
    private volatile IDataRefBackend? _backend;
    private string? _dispatchingName;
    private long _dispatchStartedTicks;

    // DataRefSubscriptionTable lives in Core (shared with the SimConnect layer).
    public ProsimDataRefService(IOptionsMonitor<ProsimOptions> options, ILogger<ProsimDataRefService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _logger = logger;
        _table = new DataRefSubscriptionTable(
            (name, ex) => _logger.LogError(ex, "Subscriber handler for {DataRef} threw", name));

        _table.RegistrationNeeded += (name, interval) => _backend?.EnsureRegistered(name, interval);
        _table.RegistrationReleased += name => _backend?.Unregister(name);

        _presses = Channel.CreateUnbounded<PressRequest>(new UnboundedChannelOptions
        {
            SingleReader = true,
        });
        _pressWorker = Task.Run(() => RunPressWorkerAsync(_shutdown.Token));

        _pushes = Channel.CreateUnbounded<PushItem>(new UnboundedChannelOptions
        {
            SingleReader = true,
        });
        _pushDispatcher = Task.Run(() => RunPushDispatcherAsync(_shutdown.Token));
    }

    /// <inheritdoc />
    public IDataRefSubscription Subscribe(string name, DataRefTier tier) => _table.Subscribe(name, tier);

    /// <inheritdoc />
    public async Task WriteAsync(string name, object? value, CancellationToken cancellationToken = default)
    {
        ProsimWriteGate.EnsureAllowed(name);
        var backend = RequireBackend();
        await backend.WriteValueAsync(name, value, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task PressMomentaryAsync(string name, CancellationToken cancellationToken = default)
    {
        ProsimWriteGate.EnsureAllowed(name);
        _ = RequireBackend();

        var request = new PressRequest(name, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), cancellationToken);
        await _presses.Writer.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        await request.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Called by the SDK session on connect; replays all active registrations.</summary>
    internal void AttachBackend(IDataRefBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        _backend = backend;

        foreach (var (name, intervalMs) in _table.ActiveRegistrations())
        {
            backend.EnsureRegistered(name, intervalMs);
        }
    }

    /// <summary>Called by the SDK session on disconnect; cached values become stale, not cleared.
    /// The stale flags queue behind pending pushes so already-received values still apply first.</summary>
    internal void DetachBackend()
    {
        _backend = null;
        _pushes.Writer.TryWrite(PushItem.MarkStale());
    }

    /// <summary>
    /// Called by the SDK session for every pushed update (on the SDK's receive thread). Only
    /// enqueues — subscriber callbacks run on the dispatcher task, so a slow consumer can never
    /// stall the SDK receive loop and freeze every cache with it (the 2026-08-09 wedge, issue #35).
    /// </summary>
    internal void UpdateFromPush(string name, object? value)
        => _pushes.Writer.TryWrite(PushItem.Push(name, value, DateTimeOffset.UtcNow));

    /// <summary>
    /// Reports a subscriber callback that has been blocking the push dispatcher for at least
    /// <paramref name="stallThresholdMs"/>. <paramref name="startedTicks"/> identifies the stall
    /// (same value while the same dispatch is stuck) so the watchdog can log it exactly once.
    /// </summary>
    internal bool TryGetStalledDispatch(long stallThresholdMs, out string dataRef, out long startedTicks)
    {
        startedTicks = Volatile.Read(ref _dispatchStartedTicks);
        if (startedTicks != 0 && Environment.TickCount64 - startedTicks >= stallThresholdMs)
        {
            dataRef = Volatile.Read(ref _dispatchingName) ?? "(unknown)";
            return true;
        }

        dataRef = "";
        startedTicks = 0;
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        _presses.Writer.TryComplete();
        _pushes.Writer.TryComplete();
        try
        {
            await _pressWorker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }

        // Bounded: a subscriber blocked inside a dispatch would otherwise hang shutdown (the
        // 2026-08-09 wedge needed the forced-process-exit backstop for exactly this reason).
        try
        {
            await _pushDispatcher.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
        catch (TimeoutException)
        {
            _logger.LogWarning(
                "Push dispatcher did not stop — a subscriber callback is blocked in {DataRef}",
                Volatile.Read(ref _dispatchingName));
        }
        _shutdown.Dispose();
    }

    private IDataRefBackend RequireBackend()
        => _backend ?? throw new InvalidOperationException(
            "ProSim is not connected — the write cannot be delivered. Callers should treat this " +
            "as a transient condition and retry once the ProSim subsystem reports Connected.");

    private async Task RunPressWorkerAsync(CancellationToken shutdownToken)
    {
        await foreach (var request in _presses.Reader.ReadAllAsync(shutdownToken).ConfigureAwait(false))
        {
            if (request.CallerToken.IsCancellationRequested)
            {
                request.Completion.TrySetCanceled(request.CallerToken);
                continue;
            }

            var options = _options.CurrentValue;
            try
            {
                _logger.LogDebug("Momentary press on {DataRef}", request.Name);
                var backend = RequireBackend();
                await backend.WriteValueAsync(request.Name, 1, shutdownToken).ConfigureAwait(false);
                await Task.Delay(options.MomentaryPressHoldMs, shutdownToken).ConfigureAwait(false);
                await backend.WriteValueAsync(request.Name, 0, shutdownToken).ConfigureAwait(false);
                request.Completion.TrySetResult();
            }
            catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
            {
                request.Completion.TrySetCanceled(shutdownToken);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Momentary press on {DataRef} failed", request.Name);
                request.Completion.TrySetException(ex);
            }

            // Gap between presses so consecutive presses can never blur together in ProSim.
            await Task.Delay(options.MomentaryPressGapMs, shutdownToken).ConfigureAwait(false);
        }
    }

    private async Task RunPushDispatcherAsync(CancellationToken shutdownToken)
    {
        await foreach (var item in _pushes.Reader.ReadAllAsync(shutdownToken).ConfigureAwait(false))
        {
            Volatile.Write(ref _dispatchingName, item.Name ?? "(mark-stale)");
            Volatile.Write(ref _dispatchStartedTicks, Environment.TickCount64);
            try
            {
                if (item.Name is null)
                {
                    _table.MarkAllStale();
                }
                else
                {
                    _table.UpdateValue(item.Name, item.Value, item.TimestampUtc);
                }
            }
            catch (Exception ex)
            {
                // The table already contains subscriber exceptions; this guards the dispatcher
                // itself so one bad update can never stop all future dispatch.
                _logger.LogError(ex, "Dispatching pushed update for {DataRef} failed", item.Name);
            }
            finally
            {
                Volatile.Write(ref _dispatchStartedTicks, 0);
            }
        }
    }

    private sealed record PressRequest(string Name, TaskCompletionSource Completion, CancellationToken CallerToken);

    /// <summary>A queued push, or the detach marker (null name) that flags all caches stale in order.</summary>
    private readonly record struct PushItem(string? Name, object? Value, DateTimeOffset TimestampUtc)
    {
        public static PushItem Push(string name, object? value, DateTimeOffset timestampUtc) => new(name, value, timestampUtc);

        public static PushItem MarkStale() => new(null, null, default);
    }
}
