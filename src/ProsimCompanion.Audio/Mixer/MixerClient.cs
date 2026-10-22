using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Logging;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Audio.Mixer;

/// <summary>
/// The VoicemeeterBridge WebSocket client for ONE agent (protocol v1,
/// <c>claude/mixer-protocol.md</c>): connect → hello → welcome, receive loop, watch-list
/// replay on every new session, set/result correlation with a timeout, and a doubling
/// reconnect backoff. Shaped like the GSX Remote API client. Since #178 the client is a
/// plain session object owned by <see cref="MixerClientHost"/>: it is built for one agent
/// block, runs <see cref="RunAsync"/> until cancelled, and is thrown away when the block
/// changes — so it never watches the options for its own endpoint. Nothing here throws into
/// a caller: a set while unreachable returns <c>not_connected</c>.
/// </summary>
public sealed class MixerClient : IMixerClient, IDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private const int MaxFrameBytes = 64 * 1024;

    private readonly MixerAgentOptions _agent;
    private readonly IOptionsMonitor<MixerOptions> _section;
    private readonly MixerStatusStore _store;
    private readonly IWireTrace _wire;
    private readonly ILogger<MixerClient> _logger;
    private readonly string _wireChannel;

    private readonly ConcurrentDictionary<string, TaskCompletionSource<MixerSetResult>> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<MixerValuesFrame>> _pendingGets = new(StringComparer.Ordinal);
    private IReadOnlyList<MixerChannel> _channels = [];
    private long _getCounter;
    private readonly ConcurrentDictionary<string, MixerValue> _values = new(StringComparer.Ordinal);
    private readonly HashSet<string> _watchList = new(StringComparer.Ordinal);
    private readonly HashSet<string> _warnedUnreadable = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _gate = new();

    private ClientWebSocket? _socket;
    private volatile bool _welcomed;
    private long _setCounter;
    private long _consecutiveConnectFailures;
    private int _failedSets;
    private string? _lastError;
    private DateTimeOffset? _connectedSinceUtc;

    public MixerClient(
        MixerAgentOptions agent,
        IOptionsMonitor<MixerOptions> section,
        MixerStatusStore store,
        IWireTrace wire,
        ILogger<MixerClient> logger)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(wire);
        ArgumentNullException.ThrowIfNull(logger);

        _agent = agent;
        _section = section;
        _store = store;
        _wire = wire;
        _logger = logger;
        Name = agent.Name.Trim();
        Endpoint = $"{agent.Host.Trim()}:{agent.Port}";
        _wireChannel = $"Mixer/{Name}";
    }

    public string Name { get; }

    public string Endpoint { get; }

    /// <summary>The block this client was built for. The host compares
    /// <see cref="SessionStampOf"/> on every options change and replaces the client when the
    /// connection half differs.</summary>
    public MixerAgentOptions Agent => _agent;

    public MixerConnectionState State { get; private set; } = MixerConnectionState.Disabled;

    public MixerVoicemeeterStatus Voicemeeter { get; private set; } = MixerVoicemeeterStatus.Unknown;

    public event EventHandler? StateChanged;

    public event EventHandler<MixerParameterChangedEventArgs>? ParameterChanged;

    public IReadOnlyList<MixerChannel> Channels => _channels;

    public event EventHandler? ChannelsChanged;

    /// <summary>What a live session is built from; a change in any part means a new client.
    /// Held in memory only, never logged.</summary>
    public static string SessionStampOf(MixerAgentOptions agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        return $"{agent.Enabled}|{agent.Host.Trim()}:{agent.Port}|{agent.Token}";
    }

    public bool TryGetValue(string parameter, out MixerValue value) => _values.TryGetValue(parameter, out value);

    public IReadOnlyDictionary<string, MixerValue> Values() => new Dictionary<string, MixerValue>(_values, StringComparer.Ordinal);

    public void Watch(IEnumerable<string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        List<string> added = [];
        lock (_gate)
        {
            foreach (var parameter in parameters)
            {
                if (!string.IsNullOrWhiteSpace(parameter) && _watchList.Add(parameter))
                {
                    added.Add(parameter);
                }
            }
        }

        if (added.Count > 0 && _welcomed && _socket is { State: WebSocketState.Open } socket)
        {
            FireAndForget(SendTextAsync(socket, MixerFrame.BuildWatch(added), CancellationToken.None), "watch");
        }
    }

    public void Unwatch(IEnumerable<string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        List<string> removed = [];
        lock (_gate)
        {
            foreach (var parameter in parameters)
            {
                if (_watchList.Remove(parameter))
                {
                    removed.Add(parameter);
                }
            }
        }

        if (removed.Count > 0 && _welcomed && _socket is { State: WebSocketState.Open } socket)
        {
            FireAndForget(SendTextAsync(socket, MixerFrame.BuildUnwatch(removed), CancellationToken.None), "unwatch");
        }
    }

    public async Task<MixerSetResult> SetAsync(string parameter, double value, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(parameter))
        {
            return MixerSetResult.Failed("invalid parameter name");
        }

        var socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open || !_welcomed)
        {
            return Complete(parameter, value, MixerSetResult.Failed("not_connected"));
        }

        var id = $"s{Interlocked.Increment(ref _setCounter)}";
        var waiter = new TaskCompletionSource<MixerSetResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = waiter;

        try
        {
            await SendTextAsync(socket, MixerFrame.BuildSet(id, parameter, value), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
        {
            _pending.TryRemove(id, out _);
            return Complete(parameter, value, MixerSetResult.Failed("not_connected"));
        }

        try
        {
            var result = await waiter.Task.WaitAsync(SetTimeout, cancellationToken).ConfigureAwait(false);
            return Complete(parameter, value, result);
        }
        catch (TimeoutException)
        {
            _pending.TryRemove(id, out _);
            return Complete(parameter, value, MixerSetResult.Failed("timeout"));
        }
        catch (OperationCanceledException)
        {
            _pending.TryRemove(id, out _);
            return Complete(parameter, value, MixerSetResult.Failed("cancelled"));
        }
    }

    private TimeSpan SetTimeout => TimeSpan.FromMilliseconds(Math.Max(250, _section.CurrentValue.SetTimeoutMs));

    /// <summary>
    /// The session loop: connect, talk until the socket drops, back off, repeat — until the
    /// token is cancelled (the host retires the client). A disabled block or an empty host
    /// never gets here: the host does not start such a client.
    /// </summary>
    public async Task RunAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunSessionAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or InvalidOperationException or SocketException or OperationCanceledException or UriFormatException)
            {
                // Quiet backoff: the first consecutive failure warns, the rest are Debug — a
                // mixer PC that is off must not flood the log.
                var failures = Interlocked.Increment(ref _consecutiveConnectFailures);
                _lastError = ex.Message;
                if (failures == 1)
                {
                    _logger.LogWarning("Mixer [{Agent}] agent unavailable at {Endpoint}: {Message}; retrying quietly", Name, Endpoint, ex.Message);
                }
                else
                {
                    _logger.LogDebug("Mixer [{Agent}] connect/receive failed (attempt {Attempts}): {Message}", Name, failures, ex.Message);
                }
            }
            finally
            {
                TearDownSession(stoppingToken.IsCancellationRequested);
            }

            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                var section = _section.CurrentValue;
                await Task.Delay(
                    MixerReconnectBackoff.Delay(
                        section.ReconnectDelayMs,
                        section.ReconnectMaxDelayMs,
                        Interlocked.Read(ref _consecutiveConnectFailures)),
                    stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        SetState(MixerConnectionState.Disabled, null);
    }

    private async Task RunSessionAsync(CancellationToken stoppingToken)
    {
        var uri = MixerProbe.BuildUri(_agent.Host, _agent.Port);

        var socket = new ClientWebSocket();
        _socket = socket;
        _welcomed = false;
        _warnedUnreadable.Clear();
        SetState(MixerConnectionState.Connecting, null);

        using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
        {
            connectCts.CancelAfter(ConnectTimeout);
            await socket.ConnectAsync(uri, connectCts.Token).ConfigureAwait(false);
        }

        _logger.LogInformation("Mixer [{Agent}] connected to the mixer agent at {Uri}; sending hello", Name, uri);
        await SendTextAsync(socket, MixerFrame.BuildHello(_agent.Token), stoppingToken, MixerFrame.RedactedHello()).ConfigureAwait(false);

        await ReceiveLoopAsync(socket, stoppingToken).ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken stoppingToken)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();

        while (!stoppingToken.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            message.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, stoppingToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    var reason = socket.CloseStatusDescription;
                    _lastError = string.IsNullOrEmpty(reason) ? $"closed ({socket.CloseStatus})" : reason;
                    if (socket.CloseStatus == WebSocketCloseStatus.PolicyViolation)
                    {
                        // 1008: hello refused (wrong token, wrong protocol). Worth a warning
                        // every time — the fix is on the settings page, not in waiting.
                        _logger.LogWarning("Mixer [{Agent}] agent refused the session: {Reason}", Name, _lastError);
                    }
                    else
                    {
                        _logger.LogInformation("Mixer [{Agent}] agent closed the connection: {Reason}", Name, _lastError);
                    }

                    return;
                }

                if (message.Length + result.Count > MaxFrameBytes)
                {
                    throw new WebSocketException("mixer frame exceeds 64 KiB");
                }

                message.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            if (_wire.Enabled)
            {
                _wire.Trace(_wireChannel, "<<", text);
            }

            try
            {
                HandleFrame(text);
            }
            catch (Exception ex)
            {
                // One bad frame must never take the session down.
                _logger.LogError(ex, "Mixer [{Agent}] failed to process a frame", Name);
            }
        }
    }

    private void HandleFrame(string text)
    {
        switch (MixerFrame.Parse(text))
        {
            case MixerWelcomeFrame welcome:
                HandleWelcome(welcome);
                break;
            case MixerStatusFrame status:
                Voicemeeter = status.Voicemeeter;
                _logger.LogInformation(
                    "Mixer [{Agent}]: Voicemeeter {State}{Detail}",
                    Name,
                    status.Voicemeeter.Connected ? "connected" : "disconnected",
                    status.Voicemeeter.Connected ? $" ({status.Voicemeeter.Kind} {status.Voicemeeter.Version})" : "");
                PublishState();
                break;
            case MixerValuesFrame values:
                foreach (var (parameter, value) in values.Values)
                {
                    Publish(parameter, value);
                }

                if (values.Id is { Length: > 0 } getId && _pendingGets.TryRemove(getId, out var getWaiter))
                {
                    // A get's errors are the caller's business (the channel probe expects
                    // them for the indices this edition lacks) — no warning.
                    getWaiter.TrySetResult(values);
                    break;
                }

                foreach (var (parameter, reason) in values.Errors)
                {
                    if (_warnedUnreadable.Add(parameter))
                    {
                        _logger.LogWarning("Mixer [{Agent}]: parameter {Parameter} cannot be read: {Reason}", Name, parameter, reason);
                    }
                }

                break;
            case MixerChangedFrame changed:
                Publish(changed.Parameter, changed.Value);
                break;
            case MixerResultFrame { Id: { Length: > 0 } id } result:
                if (_pending.TryRemove(id, out var waiter))
                {
                    waiter.TrySetResult(result.Ok ? MixerSetResult.Success : MixerSetResult.Failed(result.Error ?? "error"));
                }

                break;
            case MixerErrorFrame error:
                _logger.LogWarning("Mixer [{Agent}] agent rejected a frame: {Error}", Name, error.Error);
                break;
            default:
                break;
        }
    }

    private void HandleWelcome(MixerWelcomeFrame welcome)
    {
        if (welcome.Protocol != MixerFrame.Protocol)
        {
            _logger.LogWarning("Mixer [{Agent}] agent speaks protocol {Version}; only {Supported} is supported", Name, welcome.Protocol, MixerFrame.Protocol);
        }

        Interlocked.Exchange(ref _consecutiveConnectFailures, 0);
        _lastError = null;
        _connectedSinceUtc = DateTimeOffset.UtcNow;
        Voicemeeter = welcome.Voicemeeter;
        _welcomed = true;
        _logger.LogInformation(
            "Mixer [{Agent}] agent welcome: Voicemeeter {State}{Detail}",
            Name,
            welcome.Voicemeeter.Connected ? "connected" : "not running",
            welcome.Voicemeeter.Connected ? $" ({welcome.Voicemeeter.Kind} {welcome.Voicemeeter.Version})" : "");
        SetState(MixerConnectionState.Connected, null);

        // Replay the watch list on every new session — a fresh connection has an empty one.
        string[] watched;
        lock (_gate)
        {
            watched = [.. _watchList];
        }

        if (watched.Length > 0 && _socket is { } socket)
        {
            FireAndForget(SendTextAsync(socket, MixerFrame.BuildWatch(watched), CancellationToken.None), "watch replay");
        }

        // Strip/bus names for the editors — one get per session, off the receive path.
        _ = RefreshChannelsAsync(CancellationToken.None);
    }

    public async Task<IReadOnlyList<MixerChannel>> RefreshChannelsAsync(CancellationToken cancellationToken = default)
    {
        var names = new List<string>(MixerChannelNames.MaxIndex * 2);
        for (var i = 0; i < MixerChannelNames.MaxIndex; i++)
        {
            names.Add(MixerChannelNames.Parameter(false, i, "Label"));
            names.Add(MixerChannelNames.Parameter(true, i, "Label"));
        }

        var reply = await GetValuesAsync(names, cancellationToken).ConfigureAwait(false);
        if (reply is null)
        {
            return _channels;
        }

        var channels = new List<MixerChannel>();
        for (var i = 0; i < MixerChannelNames.MaxIndex; i++)
        {
            if (reply.Values.TryGetValue(MixerChannelNames.Parameter(false, i, "Label"), out var strip))
            {
                channels.Add(new MixerChannel(false, i, strip.Text ?? ""));
            }
        }

        for (var i = 0; i < MixerChannelNames.MaxIndex; i++)
        {
            if (reply.Values.TryGetValue(MixerChannelNames.Parameter(true, i, "Label"), out var bus))
            {
                channels.Add(new MixerChannel(true, i, bus.Text ?? ""));
            }
        }

        _channels = channels;
        _logger.LogInformation(
            "Mixer [{Agent}] channels: {Strips} strips, {Buses} buses",
            Name,
            channels.Count(c => !c.IsBus),
            channels.Count(c => c.IsBus));
        ChannelsChanged?.Invoke(this, EventArgs.Empty);
        return channels;
    }

    /// <summary>One <c>get</c>, correlated by id; null when not connected, on timeout, or when
    /// the session drops first — never throws.</summary>
    private async Task<MixerValuesFrame?> GetValuesAsync(IReadOnlyList<string> parameters, CancellationToken cancellationToken)
    {
        var socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open || !_welcomed)
        {
            return null;
        }

        var id = $"g{Interlocked.Increment(ref _getCounter)}";
        var waiter = new TaskCompletionSource<MixerValuesFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingGets[id] = waiter;
        try
        {
            await SendTextAsync(socket, MixerFrame.BuildGet(id, parameters), cancellationToken).ConfigureAwait(false);
            return await waiter.Task.WaitAsync(SetTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            _pendingGets.TryRemove(id, out _);
            _logger.LogDebug("Mixer [{Agent}] get ({Id}) failed: {Message}", Name, id, ex.Message);
            return null;
        }
    }

    private void Publish(string parameter, MixerValue value)
    {
        _values[parameter] = value;
        ParameterChanged?.Invoke(this, new MixerParameterChangedEventArgs(parameter, value));
    }

    private MixerSetResult Complete(string parameter, double value, MixerSetResult result)
    {
        if (result.Ok)
        {
            _logger.LogDebug("Mixer [{Agent}] set {Parameter}={Value} ok", Name, parameter, MixerFrame.Format(value));
        }
        else
        {
            Interlocked.Increment(ref _failedSets);
            _logger.LogWarning("Mixer [{Agent}] set {Parameter}={Value} failed: {Code}", Name, parameter, MixerFrame.Format(value), result.Code);
            PublishState();
        }

        return result;
    }

    private void SetState(MixerConnectionState state, string? reason)
    {
        if (state != State)
        {
            State = state;
            _logger.LogInformation("Mixer [{Agent}] connection: {State}", Name, state);
        }

        if (state != MixerConnectionState.Connected)
        {
            _connectedSinceUtc = null;
        }

        if (reason is not null)
        {
            _lastError = reason;
        }

        PublishState();
    }

    /// <summary>This agent's entry, as the status card shows it.</summary>
    public MixerAgentStatus Status => new(Name, Endpoint, State, Voicemeeter, _lastError, _connectedSinceUtc, _failedSets);

    private void PublishState()
    {
        var status = Status;
        _store.Update(s => s.WithAgent(status));
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task SendTextAsync(ClientWebSocket socket, string text, CancellationToken cancellationToken, string? traceAs = null)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_wire.Enabled)
            {
                _wire.Trace(_wireChannel, ">>", traceAs ?? text);
            }

            await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private void FireAndForget(Task send, string what)
    {
        _ = send.ContinueWith(
            task => _logger.LogDebug("Mixer [{Agent}] {What} send failed: {Message}", Name, what, task.Exception?.GetBaseException().Message),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private void TearDownSession(bool retiring)
    {
        var socket = _socket;
        _socket = null;
        _welcomed = false;
        socket?.Dispose();

        // Complete every in-flight set and get so callers never hang across a reconnect.
        foreach (var id in _pending.Keys.ToArray())
        {
            if (_pending.TryRemove(id, out var waiter))
            {
                waiter.TrySetResult(MixerSetResult.Failed("not_connected"));
            }
        }

        foreach (var id in _pendingGets.Keys.ToArray())
        {
            if (_pendingGets.TryRemove(id, out var waiter))
            {
                waiter.TrySetCanceled();
            }
        }

        SetState(retiring ? MixerConnectionState.Disabled : MixerConnectionState.Disconnected, null);
    }

    public void Dispose()
    {
        _socket?.Dispose();
        _sendLock.Dispose();
    }
}
