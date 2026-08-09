using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using ProSimSDK;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;
using ProsimCompanion.Prosim.DataRefs;

namespace ProsimCompanion.Prosim.Sdk;

/// <summary>
/// One live ProSim SDK session: owns the <see cref="ProSimConnect"/>, its DataRef registrations,
/// and reconnect behaviour. All SDK events arrive on arbitrary (non-GUI) SDK threads.
///
/// Connection model (per the SDK docs): <c>Connect(host, synchronous: false)</c> returns
/// immediately and the SDK keeps retrying by itself (onFailedToConnect fires per attempt) — so we
/// never stack Connect calls while it is trying. Only after an *established* connection drops
/// (onDisconnect) do we re-arm a single delayed Connect.
///
/// Threading (issue #35, 2026-08-09 wedge): <c>DataRef.Register()</c>/<c>Dispose()</c> are
/// synchronous network round-trips completed by the SDK's receive thread. Doing them inline under
/// <c>_gate</c> deadlocked against that thread and silently froze every dataref cache. All
/// register/unregister work therefore runs on a single worker task, callers only enqueue, and
/// <c>_gate</c> is held for dictionary bookkeeping only — never across SDK I/O. A watchdog
/// declares the session wedged when a round-trip stalls and signals <see cref="WedgedTask"/> so
/// the owner can rebuild the session (a fresh <see cref="ProSimConnect"/> — the SDK doc forbids
/// stacking Connect calls on a live one).
/// </summary>
internal sealed class SdkConnection : IDataRefBackend, IDisposable
{
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WorkerDrainTimeout = TimeSpan.FromSeconds(2);

    private readonly ProsimOptions _options;
    private readonly ProsimDataRefService _dataRefs;
    private readonly ConnectionStatusStore _status;
    private readonly ILogger _logger;
    private readonly ProSimConnect _connection;
    private readonly object _gate = new();
    private readonly Dictionary<string, DataRef> _subscriptionRefs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DataRef> _writeRefs = new(StringComparer.Ordinal);
    private readonly Channel<RegistrationWork> _registrationWork;
    private readonly Task _registrationWorker;
    private readonly TaskCompletionSource _wedged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Timer _watchdog;
    private Timer? _reconnectTimer;
    private volatile bool _disposed;
    private volatile bool _connected;
    private long _failedAttempts;
    private string? _workDescription;
    private long _workStartedTicks;
    private long _lastPushTicks;
    private long _reportedDispatchStallTicks;
    private bool _silenceWarned;

    public SdkConnection(
        ProsimOptions options,
        ProsimDataRefService dataRefs,
        ConnectionStatusStore status,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(dataRefs);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _dataRefs = dataRefs;
        _status = status;
        _logger = logger;

        _connection = string.IsNullOrWhiteSpace(options.ApiKey)
            ? new ProSimConnect()
            : new ProSimConnect(options.ApiKey);

        _connection.onConnect += OnConnect;
        _connection.onDisconnect += OnDisconnect;
        _connection.onFailedToConnect += OnFailedToConnect;

        _registrationWork = Channel.CreateUnbounded<RegistrationWork>(new UnboundedChannelOptions
        {
            SingleReader = true,
        });
        _registrationWorker = Task.Run(RunRegistrationWorkerAsync);
        _watchdog = new Timer(OnWatchdogTick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Completes when the session is unrecoverably wedged (a registration round-trip
    /// stalled past the configured threshold); the owner should dispose and rebuild.</summary>
    public Task WedgedTask => _wedged.Task;

    /// <summary>Starts the (self-retrying, non-blocking) connection attempt.</summary>
    public void Start()
    {
        _status.Set(Subsystems.Prosim, ConnectionState.Connecting);
        _logger.LogInformation("Connecting to ProSim at {Host}", _options.Host);
        _connection.Connect(_options.Host, false);
        _watchdog.Change(WatchdogInterval, WatchdogInterval);
    }

    void IDataRefBackend.EnsureRegistered(string name, int intervalMs)
    {
        if (!_disposed)
        {
            _registrationWork.Writer.TryWrite(RegistrationWork.Register(name, intervalMs));
        }
    }

    void IDataRefBackend.Unregister(string name)
    {
        if (!_disposed)
        {
            _registrationWork.Writer.TryWrite(RegistrationWork.Unregister(name));
        }
    }

    Task IDataRefBackend.WriteValueAsync(string name, object? value, CancellationToken cancellationToken)
    {
        // The value setter is a network call — keep it off the caller's context.
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dataRef = GetOrCreateWriteRef(name);

            // A freshly-registered ref reports Initializing until the server validates it —
            // writing in that window throws InvalidData. Wait briefly for Valid.
            var deadline = Environment.TickCount64 + 1500;
            while (dataRef.DataRefState == DataRefStateEnum.Initializing
                && Environment.TickCount64 < deadline)
            {
                Thread.Sleep(50);
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (dataRef.DataRefState == DataRefStateEnum.Error)
            {
                throw new InvalidOperationException(
                    $"ProSim does not recognize dataref '{name}' — write refused (server-side validation failed).");
            }

            try
            {
                dataRef.value = value;
            }
            catch (InvalidData) when (value is bool boolValue)
            {
                // Some refs are numerically typed even for on/off semantics; retry as 0/1
                // (the predecessors wrote these via the gateway's writeBool, so the SDK-side
                // type is not always boolean).
                dataRef.value = boolValue ? 1 : 0;
                _logger.LogDebug("Dataref {DataRef} rejected bool; coerced to int", name);
            }

            _logger.LogDebug("Wrote {Value} to dataref {DataRef}", value, name);
        }, cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _watchdog.Dispose();
        _reconnectTimer?.Dispose();

        _connection.onConnect -= OnConnect;
        _connection.onDisconnect -= OnDisconnect;
        _connection.onFailedToConnect -= OnFailedToConnect;

        _dataRefs.DetachBackend();
        _registrationWork.Writer.TryComplete();

        // Bounded: if the worker is wedged mid-round-trip we must not hang shutdown/rebuild on
        // it. Skipping graceful ref disposal is safe — closing the socket releases everything
        // server-side, and the wedged worker dies with the process (or leaks one task, once,
        // on a session rebuild).
        if (_registrationWorker.Wait(WorkerDrainTimeout))
        {
            ReleaseAllRefs();
        }
        else
        {
            _logger.LogWarning(
                "SDK registration worker did not drain (stuck in {Work}); skipping graceful dataref disposal",
                Volatile.Read(ref _workDescription));
        }

        _connection.Dispose();
    }

    private void OnConnect()
    {
        _logger.LogInformation("Connected to ProSim at {Host}", _options.Host);
        Interlocked.Exchange(ref _failedAttempts, 0);
        Volatile.Write(ref _lastPushTicks, Environment.TickCount64);
        _connected = true;
        _status.Set(Subsystems.Prosim, ConnectionState.Connected);

        // Attaching replays every active registration through EnsureRegistered.
        _dataRefs.AttachBackend(this);
    }

    private void OnDisconnect()
    {
        if (_disposed)
        {
            return;
        }

        _logger.LogWarning("Lost connection to ProSim; reconnecting in {Interval} ms", _options.ReconnectIntervalMs);
        _connected = false;
        _status.Set(Subsystems.Prosim, ConnectionState.Disconnected);
        _dataRefs.DetachBackend();

        // Queued so it serializes with in-flight registrations and any reconnect replay after it.
        _registrationWork.Writer.TryWrite(RegistrationWork.ReleaseAll());
        ArmReconnect();
    }

    private void OnFailedToConnect()
    {
        // The SDK keeps retrying by itself; log the first failure and then only every 30th so a
        // sim PC left running without ProSim does not flood the log.
        var attempts = Interlocked.Increment(ref _failedAttempts);
        if (attempts == 1 || attempts % 30 == 0)
        {
            _logger.LogInformation(
                "ProSim at {Host} is not reachable yet (attempt {Attempts}); the SDK keeps retrying",
                _options.Host,
                attempts);
        }
    }

    private void ArmReconnect()
    {
        _reconnectTimer?.Dispose();
        _reconnectTimer = new Timer(_ =>
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                _status.Set(Subsystems.Prosim, ConnectionState.Connecting);
                _connection.Connect(_options.Host, false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Reconnect attempt to ProSim failed to start");
                ArmReconnect();
            }
        }, null, _options.ReconnectIntervalMs, Timeout.Infinite);
    }

    private void OnDataChange(DataRef dataRef)
    {
        Volatile.Write(ref _lastPushTicks, Environment.TickCount64);
        try
        {
            _dataRefs.UpdateFromPush(dataRef.name, dataRef.value);
        }
        catch (Exception ex)
        {
            // Never let a consumer failure propagate into the SDK's push pump.
            _logger.LogError(ex, "Processing pushed update for {DataRef} failed", dataRef.name);
        }
    }

    private async Task RunRegistrationWorkerAsync()
    {
        await foreach (var work in _registrationWork.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            Volatile.Write(ref _workDescription, work.Describe());
            Volatile.Write(ref _workStartedTicks, Environment.TickCount64);
            try
            {
                switch (work.Kind)
                {
                    case RegistrationWorkKind.Register:
                        DoRegister(work.Name, work.IntervalMs);
                        break;
                    case RegistrationWorkKind.Unregister:
                        DoUnregister(work.Name);
                        break;
                    case RegistrationWorkKind.ReleaseAll:
                        ReleaseAllRefs();
                        break;
                }
            }
            catch (DataRefNotFoundException)
            {
                // Some refs (e.g. efb.prelimLoadsheet) exist only after first write; they will be
                // picked up on the next reconnect's registration replay.
                _logger.LogWarning("ProSim does not (yet) know dataref {DataRef}; subscription skipped", work.Name);
            }
            catch (ProSimException ex)
            {
                _logger.LogError(ex, "SDK registration work {Work} failed", work.Describe());
            }
            catch (Exception ex)
            {
                // The worker must survive anything — it is the only thread doing SDK I/O.
                _logger.LogError(ex, "SDK registration work {Work} failed unexpectedly", work.Describe());
            }
            finally
            {
                Volatile.Write(ref _workStartedTicks, 0);
            }
        }
    }

    private void DoRegister(string name, int intervalMs)
    {
        DataRef? replaced = null;
        lock (_gate)
        {
            if (_subscriptionRefs.TryGetValue(name, out var existing))
            {
                if (existing.interval <= intervalMs)
                {
                    return;
                }

                _subscriptionRefs.Remove(name);
                replaced = existing;
            }
        }

        replaced?.Dispose();

        // Attach onDataChange BEFORE Register() so the first push is never missed.
        var dataRef = new DataRef(name, intervalMs, _connection, false);
        dataRef.onDataChange += OnDataChange;
        dataRef.Register();

        lock (_gate)
        {
            _subscriptionRefs[name] = dataRef;
        }

        _logger.LogDebug("Registered dataref {DataRef} at {Interval} ms", name, intervalMs);
    }

    private void DoUnregister(string name)
    {
        DataRef? removed;
        lock (_gate)
        {
            _subscriptionRefs.Remove(name, out removed);
        }

        removed?.Dispose();
    }

    private DataRef GetOrCreateWriteRef(string name)
    {
        lock (_gate)
        {
            if (_subscriptionRefs.TryGetValue(name, out var subscribed))
            {
                return subscribed;
            }

            if (_writeRefs.TryGetValue(name, out var existing))
            {
                return existing;
            }
        }

        // Register outside _gate — it is a network round-trip (issue #35). Concurrent writers to
        // the same fresh ref may race; the loser is disposed below.
        var dataRef = new DataRef(name, (int)Core.Aircraft.DataRefTier.Infrequent, _connection, false);
        dataRef.Register();

        DataRef? loser = null;
        DataRef winner;
        lock (_gate)
        {
            if (_writeRefs.TryGetValue(name, out var raced))
            {
                loser = dataRef;
                winner = raced;
            }
            else
            {
                _writeRefs[name] = dataRef;
                winner = dataRef;
            }
        }

        loser?.Dispose();
        return winner;
    }

    private void ReleaseAllRefs()
    {
        List<DataRef> toDispose;
        lock (_gate)
        {
            toDispose = new List<DataRef>(_subscriptionRefs.Count + _writeRefs.Count);
            toDispose.AddRange(_subscriptionRefs.Values);
            toDispose.AddRange(_writeRefs.Values);
            _subscriptionRefs.Clear();
            _writeRefs.Clear();
        }

        foreach (var dataRef in toDispose)
        {
            try
            {
                dataRef.Dispose();
            }
            catch (Exception ex)
            {
                // Usually a dropped connection — the server side is gone anyway.
                _logger.LogDebug(ex, "Disposing dataref {DataRef} failed", dataRef.name);
            }
        }
    }

    private void OnWatchdogTick(object? state)
    {
        if (_disposed)
        {
            return;
        }

        var stallMs = Math.Max(1, _options.SdkStallSeconds) * 1000L;

        // (a) Registration round-trip wedged: the SDK session is unrecoverable from inside —
        // mark caches stale, report Disconnected, and signal the owner to rebuild.
        var workStarted = Volatile.Read(ref _workStartedTicks);
        if (workStarted != 0 && Environment.TickCount64 - workStarted >= stallMs)
        {
            if (_wedged.TrySetResult())
            {
                _logger.LogError(
                    "SDK registration work {Work} has been blocked for over {Seconds} s — the ProSim session is wedged and will be rebuilt",
                    Volatile.Read(ref _workDescription),
                    _options.SdkStallSeconds);
                _connected = false;
                _status.Set(Subsystems.Prosim, ConnectionState.Disconnected);
                _dataRefs.DetachBackend();
            }

            return;
        }

        // (b) Push dispatch stalled: a subscriber callback is blocked. No reconnect can free a
        // stuck callback, so this is loud diagnostics only (once per stall) — it names the
        // dataref, which names the subsystem to blame.
        if (_dataRefs.TryGetStalledDispatch(stallMs, out var stalledRef, out var stallTicks)
            && Interlocked.Exchange(ref _reportedDispatchStallTicks, stallTicks) != stallTicks)
        {
            _logger.LogError(
                "A subscriber callback for dataref {DataRef} has blocked the push dispatcher for over {Seconds} s — "
                + "dataref caches are frozen until it returns; an app restart may be required",
                stalledRef,
                _options.SdkStallSeconds);
        }

        // (c) Push silence: possibly legitimate (sim paused), so warn once for visibility and
        // never reconnect over it.
        if (_connected)
        {
            bool anyRefs;
            lock (_gate)
            {
                anyRefs = _subscriptionRefs.Count > 0;
            }

            var silentMs = Environment.TickCount64 - Volatile.Read(ref _lastPushTicks);
            var warnMs = Math.Max(1, _options.PushSilenceWarnSeconds) * 1000L;
            if (anyRefs && silentMs >= warnMs && !_silenceWarned)
            {
                _silenceWarned = true;
                _logger.LogWarning(
                    "No dataref pushes for {Seconds} s while connected — sim paused, ProSim idle, or the connection is dead",
                    silentMs / 1000);
            }
            else if (_silenceWarned && silentMs < warnMs)
            {
                _silenceWarned = false;
                _logger.LogInformation("Dataref pushes resumed");
            }
        }
    }

    private enum RegistrationWorkKind
    {
        Register,
        Unregister,
        ReleaseAll,
    }

    private readonly record struct RegistrationWork(RegistrationWorkKind Kind, string Name, int IntervalMs)
    {
        public static RegistrationWork Register(string name, int intervalMs) => new(RegistrationWorkKind.Register, name, intervalMs);

        public static RegistrationWork Unregister(string name) => new(RegistrationWorkKind.Unregister, name, 0);

        public static RegistrationWork ReleaseAll() => new(RegistrationWorkKind.ReleaseAll, "", 0);

        public string Describe() => Kind switch
        {
            RegistrationWorkKind.Register => $"register {Name} @ {IntervalMs} ms",
            RegistrationWorkKind.Unregister => $"unregister {Name}",
            _ => "release all refs",
        };
    }
}
