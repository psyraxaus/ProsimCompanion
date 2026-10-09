using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Logging;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Audio.Mixer;

/// <summary>
/// The VoicemeeterBridge WebSocket client (protocol v1, <c>claude/mixer-protocol.md</c>):
/// connect → hello → welcome, receive loop, watch-list replay on every new session, set/result
/// correlation with a timeout, and a doubling reconnect backoff. Shaped like the GSX Remote
/// API client. Off (or no host) means no socket at all; the loop sleeps until the options
/// change. Nothing here throws into a caller: a set while unreachable returns
/// <c>not_connected</c>, a probe returns its failure as text.
/// </summary>
public sealed class MixerClient : BackgroundService, IMixerClient
{
    private const string WireChannel = "Mixer";
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);
    private const int MaxFrameBytes = 64 * 1024;

    private readonly IOptionsMonitor<MixerOptions> _options;
    private readonly ConnectionStatusStore _status;
    private readonly MixerStatusStore _store;
    private readonly IWireTrace _wire;
    private readonly ILogger<MixerClient> _logger;

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
    private string _sessionEndpoint = "";
    private string _sessionKey = "";
    private volatile bool _welcomed;
    private long _setCounter;
    private long _consecutiveConnectFailures;
    private int _failedSets;
    private string? _lastError;
    private DateTimeOffset? _connectedSinceUtc;

    public MixerClient(
        IOptionsMonitor<MixerOptions> options,
        ConnectionStatusStore status,
        MixerStatusStore store,
        IWireTrace wire,
        ILogger<MixerClient> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(wire);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _status = status;
        _store = store;
        _wire = wire;
        _logger = logger;

        // A saved host/port/token change drops the live session so the loop reconnects with
        // the new values; the loop itself re-reads the options on every attempt.
        _options.OnChange(o =>
        {
            var socket = _socket;
            if (socket is not null && !string.Equals(SessionKeyOf(o), _sessionKey, StringComparison.Ordinal))
            {
                _logger.LogInformation("Mixer endpoint changed; reconnecting");
                socket.Abort();
            }
        });
    }

    public MixerConnectionState State { get; private set; } = MixerConnectionState.Disabled;

    public MixerVoicemeeterStatus Voicemeeter { get; private set; } = MixerVoicemeeterStatus.Unknown;

    public event EventHandler? StateChanged;

    public event EventHandler<MixerParameterChangedEventArgs>? ParameterChanged;

    public IReadOnlyList<MixerChannel> Channels => _channels;

    public event EventHandler? ChannelsChanged;

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
            var timeout = TimeSpan.FromMilliseconds(Math.Max(250, _options.CurrentValue.SetTimeoutMs));
            var result = await waiter.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
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

    public async Task<MixerProbeResult> ProbeAsync(string host, int port, string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return new MixerProbeResult(false, "Enter a host first.", null);
        }

        using var socket = new ClientWebSocket();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(ProbeTimeout);
        try
        {
            await socket.ConnectAsync(BuildUri(host, port), cts.Token).ConfigureAwait(false);
            var hello = Encoding.UTF8.GetBytes(MixerFrame.BuildHello(token));
            await socket.SendAsync(hello, WebSocketMessageType.Text, endOfMessage: true, cts.Token).ConfigureAwait(false);

            var (text, close) = await ReceiveOneAsync(socket, cts.Token).ConfigureAwait(false);
            if (close is not null)
            {
                return new MixerProbeResult(false, $"Agent refused the hello: {close}", null);
            }

            if (MixerFrame.Parse(text) is MixerWelcomeFrame welcome)
            {
                var vm = welcome.Voicemeeter;
                var detail = vm.Connected
                    ? $"Voicemeeter {vm.Kind} {vm.Version} is running."
                    : "Voicemeeter is not running on that PC.";
                await CloseQuietlyAsync(socket).ConfigureAwait(false);
                return new MixerProbeResult(true, $"Connected. {detail}", vm);
            }

            return new MixerProbeResult(false, "Unexpected first frame from the agent.", null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new MixerProbeResult(false, $"No answer from {host}:{port} within {ProbeTimeout.TotalSeconds:F0} s.", null);
        }
        catch (Exception ex) when (ex is WebSocketException or SocketException or IOException or InvalidOperationException or UriFormatException)
        {
            return new MixerProbeResult(false, $"Could not connect: {ex.Message}", null);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var options = _options.CurrentValue;
            if (!options.Enabled || string.IsNullOrWhiteSpace(options.Host))
            {
                SetState(MixerConnectionState.Disabled, options.Enabled ? "no host configured" : null);
                await WaitForOptionsChangeAsync(stoppingToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                await RunSessionAsync(options, stoppingToken).ConfigureAwait(false);
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
                    _logger.LogWarning("Mixer agent unavailable at {Endpoint}: {Message}; retrying quietly", _sessionEndpoint, ex.Message);
                }
                else
                {
                    _logger.LogDebug("Mixer connect/receive failed (attempt {Attempts}): {Message}", failures, ex.Message);
                }
            }
            finally
            {
                TearDownSession();
            }

            try
            {
                var reconnect = _options.CurrentValue;
                await Task.Delay(
                    MixerReconnectBackoff.Delay(
                        reconnect.ReconnectDelayMs,
                        reconnect.ReconnectMaxDelayMs,
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

    private async Task RunSessionAsync(MixerOptions options, CancellationToken stoppingToken)
    {
        _sessionEndpoint = EndpointOf(options);
        _sessionKey = SessionKeyOf(options);
        var uri = BuildUri(options.Host, options.Port);

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

        _logger.LogInformation("Connected to the mixer agent at {Uri}; sending hello", uri);
        await SendTextAsync(socket, MixerFrame.BuildHello(options.Token), stoppingToken, MixerFrame.RedactedHello()).ConfigureAwait(false);

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
                        _logger.LogWarning("Mixer agent refused the session: {Reason}", _lastError);
                    }
                    else
                    {
                        _logger.LogInformation("Mixer agent closed the connection: {Reason}", _lastError);
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
                _wire.Trace(WireChannel, "<<", text);
            }

            try
            {
                HandleFrame(text);
            }
            catch (Exception ex)
            {
                // One bad frame must never take the session down.
                _logger.LogError(ex, "Failed to process a mixer frame");
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
                    "Mixer: Voicemeeter {State}{Detail}",
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
                        _logger.LogWarning("Mixer: parameter {Parameter} cannot be read: {Reason}", parameter, reason);
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
                _logger.LogWarning("Mixer agent rejected a frame: {Error}", error.Error);
                break;
            default:
                break;
        }
    }

    private void HandleWelcome(MixerWelcomeFrame welcome)
    {
        if (welcome.Protocol != MixerFrame.Protocol)
        {
            _logger.LogWarning("Mixer agent speaks protocol {Version}; only {Supported} is supported", welcome.Protocol, MixerFrame.Protocol);
        }

        Interlocked.Exchange(ref _consecutiveConnectFailures, 0);
        _lastError = null;
        _connectedSinceUtc = DateTimeOffset.UtcNow;
        Voicemeeter = welcome.Voicemeeter;
        _welcomed = true;
        _logger.LogInformation(
            "Mixer agent welcome: Voicemeeter {State}{Detail}",
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
            "Mixer channels: {Strips} strips, {Buses} buses",
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
            var timeout = TimeSpan.FromMilliseconds(Math.Max(250, _options.CurrentValue.SetTimeoutMs));
            return await waiter.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            _pendingGets.TryRemove(id, out _);
            _logger.LogDebug("Mixer get ({Id}) failed: {Message}", id, ex.Message);
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
            _logger.LogDebug("Mixer set {Parameter}={Value} ok", parameter, MixerFrame.Format(value));
        }
        else
        {
            Interlocked.Increment(ref _failedSets);
            _logger.LogWarning("Mixer set {Parameter}={Value} failed: {Code}", parameter, MixerFrame.Format(value), result.Code);
            PublishState();
        }

        return result;
    }

    private void SetState(MixerConnectionState state, string? reason)
    {
        if (state != State)
        {
            State = state;
            _logger.LogInformation("Mixer connection: {State}", state);
        }

        if (state != MixerConnectionState.Connected)
        {
            _connectedSinceUtc = null;
        }

        _status.Set(Subsystems.Mixer, state switch
        {
            MixerConnectionState.Connected => ConnectionState.Connected,
            MixerConnectionState.Connecting => ConnectionState.Connecting,
            MixerConnectionState.Disconnected => ConnectionState.Disconnected,
            _ => ConnectionState.Disabled,
        }, reason);
        PublishState();
    }

    private void PublishState()
    {
        var options = _options.CurrentValue;
        var state = State;
        var vm = Voicemeeter;
        var endpoint = EndpointOf(options);
        var error = _lastError;
        var since = _connectedSinceUtc;
        var failed = _failedSets;
        _store.Update(s => s with
        {
            Enabled = options.Enabled,
            State = state,
            Voicemeeter = vm,
            Endpoint = endpoint,
            LastError = error,
            ConnectedSinceUtc = since,
            FailedSets = failed,
        });
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
                _wire.Trace(WireChannel, ">>", traceAs ?? text);
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
            task => _logger.LogDebug("Mixer {What} send failed: {Message}", what, task.Exception?.GetBaseException().Message),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private void TearDownSession()
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

        var options = _options.CurrentValue;
        SetState(
            options.Enabled && !string.IsNullOrWhiteSpace(options.Host)
                ? MixerConnectionState.Disconnected
                : MixerConnectionState.Disabled,
            null);
    }

    private async Task WaitForOptionsChangeAsync(CancellationToken stoppingToken)
    {
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = _options.OnChange(_ => changed.TrySetResult());
        await changed.Task.WaitAsync(stoppingToken).ConfigureAwait(false);
    }

    private static async Task<(string Text, string? Close)> ReceiveOneAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return ("", string.IsNullOrEmpty(socket.CloseStatusDescription) ? socket.CloseStatus?.ToString() ?? "closed" : socket.CloseStatusDescription);
            }

            message.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return (Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length), null);
    }

    private static async Task CloseQuietlyAsync(ClientWebSocket socket)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "probe done", cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // A probe socket that will not close cleanly is simply dropped.
        }
    }

    private static Uri BuildUri(string host, int port) => new($"ws://{host.Trim()}:{port}/ws");

    /// <summary>Host:port for logs and the status page — never the token.</summary>
    private static string EndpointOf(MixerOptions options) => $"{options.Host.Trim()}:{options.Port}";

    /// <summary>What a live session was built from; a change in any part forces a reconnect.
    /// Held in memory only, never logged.</summary>
    private static string SessionKeyOf(MixerOptions options) => $"{options.Enabled}|{options.Host.Trim()}:{options.Port}|{options.Token}";

    public override void Dispose()
    {
        _sendLock.Dispose();
        base.Dispose();
    }
}
