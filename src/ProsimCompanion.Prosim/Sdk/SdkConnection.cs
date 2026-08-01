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
/// </summary>
internal sealed class SdkConnection : IDataRefBackend, IDisposable
{
    private readonly ProsimOptions _options;
    private readonly ProsimDataRefService _dataRefs;
    private readonly ConnectionStatusStore _status;
    private readonly ILogger _logger;
    private readonly ProSimConnect _connection;
    private readonly object _gate = new();
    private readonly Dictionary<string, DataRef> _subscriptionRefs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DataRef> _writeRefs = new(StringComparer.Ordinal);
    private Timer? _reconnectTimer;
    private volatile bool _disposed;
    private long _failedAttempts;

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
    }

    /// <summary>Starts the (self-retrying, non-blocking) connection attempt.</summary>
    public void Start()
    {
        _status.Set(Subsystems.Prosim, ConnectionState.Connecting);
        _logger.LogInformation("Connecting to ProSim at {Host}", _options.Host);
        _connection.Connect(_options.Host, false);
    }

    void IDataRefBackend.EnsureRegistered(string name, int intervalMs)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            lock (_gate)
            {
                if (_subscriptionRefs.TryGetValue(name, out var existing))
                {
                    if (existing.interval <= intervalMs)
                    {
                        return;
                    }

                    existing.Dispose();
                    _subscriptionRefs.Remove(name);
                }

                // Attach onDataChange BEFORE Register() so the first push is never missed.
                var dataRef = new DataRef(name, intervalMs, _connection, false);
                dataRef.onDataChange += OnDataChange;
                dataRef.Register();
                _subscriptionRefs[name] = dataRef;
                _logger.LogDebug("Registered dataref {DataRef} at {Interval} ms", name, intervalMs);
            }
        }
        catch (DataRefNotFoundException)
        {
            // Some refs (e.g. efb.prelimLoadsheet) exist only after first write; they will be
            // picked up on the next reconnect's registration replay.
            _logger.LogWarning("ProSim does not (yet) know dataref {DataRef}; subscription skipped", name);
        }
        catch (ProSimException ex)
        {
            _logger.LogError(ex, "Failed to register dataref {DataRef}", name);
        }
    }

    void IDataRefBackend.Unregister(string name)
    {
        lock (_gate)
        {
            if (_subscriptionRefs.Remove(name, out var dataRef))
            {
                dataRef.Dispose();
            }
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
        _reconnectTimer?.Dispose();

        _connection.onConnect -= OnConnect;
        _connection.onDisconnect -= OnDisconnect;
        _connection.onFailedToConnect -= OnFailedToConnect;

        _dataRefs.DetachBackend();
        DisposeAllRefs();
        _connection.Dispose();
    }

    private void OnConnect()
    {
        _logger.LogInformation("Connected to ProSim at {Host}", _options.Host);
        Interlocked.Exchange(ref _failedAttempts, 0);
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
        _status.Set(Subsystems.Prosim, ConnectionState.Disconnected);
        _dataRefs.DetachBackend();
        DisposeAllRefs();
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

            var dataRef = new DataRef(name, (int)Core.Aircraft.DataRefTier.Infrequent, _connection, false);
            dataRef.Register();
            _writeRefs[name] = dataRef;
            return dataRef;
        }
    }

    private void DisposeAllRefs()
    {
        lock (_gate)
        {
            foreach (var dataRef in _subscriptionRefs.Values)
            {
                dataRef.Dispose();
            }
            foreach (var dataRef in _writeRefs.Values)
            {
                dataRef.Dispose();
            }
            _subscriptionRefs.Clear();
            _writeRefs.Clear();
        }
    }
}
