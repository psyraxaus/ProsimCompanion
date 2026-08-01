using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Prosim.DataRefs;

/// <summary>
/// The application-wide <see cref="IProsimDataRefs"/> implementation. Owns the subscription
/// table and the serialized momentary-press worker; a connected SDK session attaches itself as
/// the <see cref="IDataRefBackend"/>. With no backend attached, cached (stale-flagged) values
/// remain readable and writes fail fast with a clear error.
/// </summary>
public sealed class ProsimDataRefService : IProsimDataRefs, IAsyncDisposable
{
    private readonly DataRefSubscriptionTable _table;
    private readonly ILogger<ProsimDataRefService> _logger;
    private readonly IOptionsMonitor<ProsimOptions> _options;
    private readonly Channel<PressRequest> _presses;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _pressWorker;
    private volatile IDataRefBackend? _backend;

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

    /// <summary>Called by the SDK session on disconnect; cached values become stale, not cleared.</summary>
    internal void DetachBackend()
    {
        _backend = null;
        _table.MarkAllStale();
    }

    /// <summary>Called by the SDK session for every pushed update (on the SDK's thread).</summary>
    internal void UpdateFromPush(string name, object? value)
        => _table.UpdateValue(name, value, DateTimeOffset.UtcNow);

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        _presses.Writer.TryComplete();
        try
        {
            await _pressWorker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
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

    private sealed record PressRequest(string Name, TaskCompletionSource Completion, CancellationToken CallerToken);
}
