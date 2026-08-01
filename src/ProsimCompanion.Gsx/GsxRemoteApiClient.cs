using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Logging;
using ProsimCompanion.Core.State;
using ProsimCompanion.Gsx.Mirror;
using ProsimCompanion.Gsx.Protocol;
using ProsimCompanion.Gsx.Transport;

namespace ProsimCompanion.Gsx;

/// <summary>Client readiness (three states — the server can be listening while GSX itself is
/// still booting; never gate on socket-connected). See docs/integrations/gsx-remote-api.md §1.3.</summary>
public enum GsxReadiness
{
    Disconnected,
    ConnectedGsxNotRunning,
    Ready,
}

/// <summary>
/// The Couatl Remote API v2 WebSocket client: connect/receive/reconnect loop, hello handshake +
/// state subscription, command correlation with timeouts, engine events, and the typed state
/// mirror. Every frame in both directions goes through the wire trace. Locked decision 3:
/// actions dispatch off this receive path — there is no tick-loop command queue.
/// </summary>
public sealed class GsxRemoteApiClient : BackgroundService, IGsxRemoteApi
{
    private const string WireChannel = "GsxRemoteApi";
    private const int RequiredProtocol = 1;

    private readonly IOptionsMonitor<GsxOptions> _options;
    private readonly ConnectionStatusStore _status;
    private readonly IWireTrace _wire;
    private readonly ILogger<GsxRemoteApiClient> _logger;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<GsxCommandResult>> _pending = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ClientWebSocket? _socket;
    private long _commandCounter;
    private long _consecutiveConnectFailures;

    // Per-session state (reset on every new socket/hello).
    private bool _gsxRunning;
    private bool _protocolSupported;
    private bool _protocolWarned;
    private IReadOnlySet<string> _capabilities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private volatile bool _restartExpected;

    public GsxRemoteApiClient(
        IOptionsMonitor<GsxOptions> options,
        ConnectionStatusStore status,
        IWireTrace wire,
        ILogger<GsxRemoteApiClient> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(wire);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _status = status;
        _wire = wire;
        _logger = logger;
    }

    /// <summary>The typed state mirror (updated from the receive path).</summary>
    public GsxStateMirror Mirror { get; } = new();

    public GsxReadiness Readiness { get; private set; } = GsxReadiness.Disconnected;

    /// <summary>Raised on readiness transitions, on the receive thread.</summary>
    public event Action<GsxReadiness>? ReadinessChanged;

    /// <summary>Raised after every command completes (server or synthetic result) — feeds the
    /// diagnostics page's recent-commands view.</summary>
    public event Action<GsxCommandView>? CommandCompleted;

    /// <summary>Capability tokens from the current session's hello.</summary>
    public IReadOnlyCollection<string> Capabilities => _capabilities;

    /// <summary>True when the hello advertised the capability (case-insensitive).</summary>
    public bool HasCapability(string token) => _capabilities.Contains(token);

    /// <summary>
    /// Sends a command and awaits its correlated result. Synthetic results (codes
    /// <c>not_connected</c>, <c>gsx_not_running</c>, <c>timeout</c>) are returned — never thrown —
    /// so callers handle all outcomes through one code path.
    /// </summary>
    public async Task<GsxCommandResult> SendCommandAsync(string verb, JsonObject? args, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verb);
        var argsText = args?.ToJsonString() ?? "";

        var socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open)
        {
            return Complete(verb, argsText, GsxCommandResult.Synthetic("not_connected"));
        }

        if (Readiness != GsxReadiness.Ready)
        {
            return Complete(verb, argsText, GsxCommandResult.Synthetic("gsx_not_running"));
        }

        var id = $"{VerbPrefix(verb)}-{Interlocked.Increment(ref _commandCounter)}";
        var frame = GsxFrame.BuildCommand(id, verb, args);
        var waiter = new TaskCompletionSource<GsxCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = waiter;

        try
        {
            await SendTextAsync(socket, frame, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            _pending.TryRemove(id, out _);
            _logger.LogWarning("Sending {Verb} failed: {Message}", verb, ex.Message);
            return Complete(verb, argsText, GsxCommandResult.Synthetic("not_connected"));
        }

        try
        {
            var timeout = TimeSpan.FromMilliseconds(_options.CurrentValue.CommandTimeoutMs);
            var result = await waiter.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return Complete(verb, argsText, result);
        }
        catch (TimeoutException)
        {
            _pending.TryRemove(id, out _);
            _logger.LogWarning("Command {Verb} ({Id}) timed out", verb, id);
            return Complete(verb, argsText, GsxCommandResult.Synthetic("timeout"));
        }
    }

    /// <summary>One compact CMTrace-friendly summary line per command + diagnostics feed; the
    /// wire trace carries the full frames.</summary>
    private GsxCommandResult Complete(string verb, string argsText, GsxCommandResult result)
    {
        _logger.LogInformation(
            "GSX command {Verb} {Args} -> {Outcome} ({Code})",
            verb,
            argsText,
            result.Ok ? "ok" : "failed",
            result.Code);
        CommandCompleted?.Invoke(new GsxCommandView(DateTimeOffset.UtcNow, verb, argsText, result.Ok, result.Code));
        return result;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var options = _options.CurrentValue;
            if (!options.Enabled)
            {
                _status.Set(Subsystems.Gsx, ConnectionState.Disabled);
                await WaitForOptionsChangeAsync(stoppingToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                await RunSessionAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or InvalidOperationException)
            {
                // Quiet backoff: first consecutive failure warns, the rest are Debug —
                // an absent GSX/sim must not flood the log. An anticipated Couatl restart
                // (engine restarting event) suppresses the warning too.
                var failures = Interlocked.Increment(ref _consecutiveConnectFailures);
                if (failures == 1 && !_restartExpected)
                {
                    _logger.LogWarning("GSX Remote API unavailable: {Message}; retrying quietly", ex.Message);
                }
                else
                {
                    _logger.LogDebug("GSX Remote API connect/receive failed (attempt {Attempts}): {Message}", failures, ex.Message);
                }
            }
            finally
            {
                TearDownSession();
            }

            try
            {
                await Task.Delay(_options.CurrentValue.ReconnectIntervalMs, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunSessionAsync(CancellationToken stoppingToken)
    {
        // The ini is re-read on EVERY attempt so a port change needs no restart.
        var port = CouatlPortLocator.LocatePort();
        var uri = new Uri($"ws://127.0.0.1:{port}");

        var socket = new ClientWebSocket();
        _socket = socket;
        await socket.ConnectAsync(uri, stoppingToken).ConfigureAwait(false);

        Interlocked.Exchange(ref _consecutiveConnectFailures, 0);
        _logger.LogInformation("Connected to GSX Remote API at {Uri}", uri);

        // Fresh session: protocol latch and capabilities reset (a restarted Couatl may differ).
        _gsxRunning = false;
        _protocolSupported = true;
        _protocolWarned = false;
        _capabilities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        UpdateReadiness();

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
                    _logger.LogInformation("GSX Remote API closed the connection");
                    return;
                }

                message.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            _wire.Trace(WireChannel, "<<", text);

            try
            {
                HandleFrame(text);
            }
            catch (Exception ex)
            {
                // One bad frame must never take the session down.
                _logger.LogError(ex, "Failed to process a GSX Remote API frame");
            }
        }
    }

    private void HandleFrame(string text)
    {
        var frame = GsxFrame.Parse(text);
        if (frame is null)
        {
            return; // unknown type / unparseable — ignored by protocol-1 additive rules
        }

        if (frame.EnvelopeVersion is { } version && version != RequiredProtocol)
        {
            WarnProtocolOnce(version);
            return;
        }

        switch (frame)
        {
            case GsxHelloFrame hello:
                HandleHello(hello);
                break;
            case GsxResultFrame { Id: { Length: > 0 } id } resultFrame:
                if (_pending.TryRemove(id, out var waiter))
                {
                    waiter.TrySetResult(GsxCommandResult.From(resultFrame));
                }
                break;
            case GsxResultFrame:
                break; // unsolicited (subscribe ack) — ignore
            case GsxSnapshotFrame snapshot:
                foreach (var (key, value) in snapshot.StateEntries)
                {
                    Mirror.ApplyState(key, value);
                }
                break;
            case GsxPatchFrame patch:
                Mirror.ApplyState(patch.Key, patch.Value);
                break;
            case GsxEngineEventFrame engineEvent:
                HandleEngineEvent(engineEvent);
                break;
        }
    }

    private void HandleHello(GsxHelloFrame hello)
    {
        // A hello on a live session means a fresh server session; clear the restart latch.
        _restartExpected = false;

        if (hello.Protocol is { } protocol && protocol != RequiredProtocol)
        {
            WarnProtocolOnce(protocol);
        }
        else
        {
            _protocolSupported = true;
        }

        _gsxRunning = hello.GsxRunning;
        _capabilities = hello.Capabilities;
        _logger.LogInformation(
            "GSX hello: protocol {Protocol}, gsxRunning {GsxRunning}, capabilities [{Capabilities}]",
            hello.Protocol,
            hello.GsxRunning,
            string.Join(", ", hello.Capabilities));

        // Subscribe to the state channel; on failure abort the socket so the reconnect loop
        // builds a clean session — a connected-but-unsubscribed client is silently starved.
        var socket = _socket;
        if (socket is not null)
        {
            var subscribe = GsxFrame.BuildSubscribe();
            _ = SendTextAsync(socket, subscribe, CancellationToken.None)
                .ContinueWith(
                    task =>
                    {
                        if (task.IsFaulted)
                        {
                            _logger.LogWarning("State subscription failed; forcing a clean reconnect");
                            socket.Abort();
                        }
                    },
                    TaskScheduler.Default);
        }

        UpdateReadiness();
    }

    private void HandleEngineEvent(GsxEngineEventFrame engineEvent)
    {
        if (engineEvent.Restarting)
        {
            // Fires BEFORE the socket drops: suppress the reconnect warning and expect a new sid.
            _restartExpected = true;
            _logger.LogInformation("GSX engine restart announced; expecting reconnect");
        }

        if (engineEvent.GsxRunning is { } running)
        {
            _gsxRunning = running;
            UpdateReadiness();
        }
    }

    private void UpdateReadiness()
    {
        var socket = _socket;
        var readiness = socket is null || socket.State != WebSocketState.Open
            ? GsxReadiness.Disconnected
            : _gsxRunning && _protocolSupported && _capabilities.Contains("gate") && _capabilities.Contains("handlerData")
                ? GsxReadiness.Ready
                : GsxReadiness.ConnectedGsxNotRunning;

        if (readiness == Readiness)
        {
            return;
        }

        Readiness = readiness;
        _logger.LogInformation("GSX Remote API readiness: {Readiness}", readiness);
        _status.Set(Subsystems.Gsx, readiness switch
        {
            GsxReadiness.Ready => ConnectionState.Connected,
            GsxReadiness.ConnectedGsxNotRunning => ConnectionState.Connecting,
            _ => ConnectionState.Disconnected,
        });
        ReadinessChanged?.Invoke(readiness);
    }

    private void WarnProtocolOnce(int version)
    {
        _protocolSupported = false;
        if (!_protocolWarned)
        {
            _protocolWarned = true;
            _logger.LogWarning(
                "GSX Remote API speaks protocol {Version}; only {Supported} is supported — holding non-Ready",
                version,
                RequiredProtocol);
        }
        UpdateReadiness();
    }

    private async Task SendTextAsync(ClientWebSocket socket, string text, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _wire.Trace(WireChannel, ">>", text);
            await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private void TearDownSession()
    {
        var socket = _socket;
        _socket = null;
        socket?.Dispose();

        // Complete every in-flight command so callers never hang across a reconnect.
        foreach (var id in _pending.Keys.ToArray())
        {
            if (_pending.TryRemove(id, out var waiter))
            {
                waiter.TrySetResult(GsxCommandResult.Synthetic("not_connected"));
            }
        }

        UpdateReadiness();
    }

    private async Task WaitForOptionsChangeAsync(CancellationToken stoppingToken)
    {
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = _options.OnChange(_ => changed.TrySetResult());
        await changed.Task.WaitAsync(stoppingToken).ConfigureAwait(false);
    }

    private static string VerbPrefix(string verb) => verb switch
    {
        "gate.select" => "g",
        "service.trigger" => "s",
        "handler.set" => "h",
        _ when verb.StartsWith("menu.", StringComparison.Ordinal) => "m",
        _ => "c",
    };
}

/// <summary>Outcome of a command: server result or client-synthesized
/// (<c>not_connected</c>/<c>gsx_not_running</c>/<c>timeout</c>). Codes are strings; unknown
/// codes round-trip verbatim.</summary>
public sealed record GsxCommandResult(bool Ok, string Code, JsonObject? Payload, JsonObject? Error)
{
    public static GsxCommandResult Synthetic(string code) => new(false, code, null, null);

    public static GsxCommandResult From(GsxResultFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return new GsxCommandResult(frame.Ok, frame.Code, frame.Payload, frame.Error);
    }
}
