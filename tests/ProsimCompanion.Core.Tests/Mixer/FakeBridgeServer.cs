using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace ProsimCompanion.Core.Tests.Mixer;

/// <summary>
/// A fake VoicemeeterBridge agent for the client tests: a raw TCP listener that performs the
/// WebSocket upgrade by hand (no HttpListener URL ACL, no Kestrel) and then speaks protocol v1
/// — hello/welcome, watch → snapshot, set → result + changed (switchable off so a test can
/// answer by hand, out of order, or never). Every received frame of every session is queued
/// so tests can assert the exact order the client sent.
/// </summary>
internal sealed class FakeBridgeServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<FakeBridgeSession> _sessions = Channel.CreateUnbounded<FakeBridgeSession>();
    private Task? _acceptLoop;

    public FakeBridgeServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
    }

    public string Token { get; set; } = "secret-token";

    /// <summary>When true (default) a <c>set</c> is answered with <c>result ok</c> and a
    /// <c>changed</c>; false leaves the reply to the test.</summary>
    public bool AutoReplySet { get; set; } = true;

    /// <summary>Values the snapshot reports for watched names (unknown names → errors).</summary>
    public Dictionary<string, double> Values { get; } = new(StringComparer.Ordinal);

    public bool VoicemeeterConnected { get; set; } = true;

    public int Port { get; private set; }

    public List<FakeBridgeSession> Sessions { get; } = [];

    public void Start()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptLoopAsync();
    }

    /// <summary>The next client session that completed the WebSocket upgrade.</summary>
    public async Task<FakeBridgeSession> NextSessionAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        return await _sessions.Reader.ReadAsync(cts.Token);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient tcp;
            try
            {
                tcp = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            _ = HandleAsync(tcp);
        }
    }

    private async Task HandleAsync(TcpClient tcp)
    {
        try
        {
            var stream = tcp.GetStream();
            var key = await ReadUpgradeKeyAsync(stream, _cts.Token);
            if (key is null)
            {
                tcp.Dispose();
                return;
            }

            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            var response = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
                + $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response), _cts.Token);

            var socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true });
            var session = new FakeBridgeSession(this, socket, tcp);
            lock (Sessions)
            {
                Sessions.Add(session);
            }

            await _sessions.Writer.WriteAsync(session, _cts.Token);
            await session.RunAsync(_cts.Token);
        }
        catch (Exception ex) when (ex is IOException or SocketException or WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // Session over — the client dropped, or the server is stopping.
        }
    }

    private static async Task<string?> ReadUpgradeKeyAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), ct);
            if (read == 0)
            {
                return null;
            }

            total += read;
            var text = Encoding.ASCII.GetString(buffer, 0, total);
            var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end < 0)
            {
                continue;
            }

            foreach (var line in text[..end].Split("\r\n"))
            {
                if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
                {
                    return line["Sec-WebSocket-Key:".Length..].Trim();
                }
            }

            return null;
        }

        return null;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        FakeBridgeSession[] sessions;
        lock (Sessions)
        {
            sessions = [.. Sessions];
        }

        foreach (var session in sessions)
        {
            session.Abort();
        }

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _cts.Dispose();
    }
}

/// <summary>One accepted connection of the fake agent.</summary>
internal sealed class FakeBridgeSession
{
    private readonly FakeBridgeServer _server;
    private readonly WebSocket _socket;
    private readonly TcpClient _tcp;
    private readonly Channel<JsonObject> _frames = Channel.CreateUnbounded<JsonObject>();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public FakeBridgeSession(FakeBridgeServer server, WebSocket socket, TcpClient tcp)
    {
        _server = server;
        _socket = socket;
        _tcp = tcp;
    }

    /// <summary>Every frame the client sent on this session, in order (the hello included).</summary>
    public List<JsonObject> Received { get; } = [];

    public bool Closed { get; private set; }

    /// <summary>The next frame the client sends, or a timeout.</summary>
    public async Task<JsonObject> NextFrameAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        return await _frames.Reader.ReadAsync(cts.Token);
    }

    public async Task SendAsync(string json)
    {
        await _sendLock.WaitAsync();
        try
        {
            await _socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public Task SendResultAsync(string id, bool ok, string? error = null) =>
        SendAsync(error is null
            ? $"{{\"op\":\"result\",\"id\":\"{id}\",\"ok\":{(ok ? "true" : "false")}}}"
            : $"{{\"op\":\"result\",\"id\":\"{id}\",\"ok\":{(ok ? "true" : "false")},\"error\":\"{error}\"}}");

    public Task SendChangedAsync(string parameter, double value) =>
        SendAsync($"{{\"op\":\"changed\",\"param\":\"{parameter}\",\"value\":{value.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}");

    public Task SendStatusAsync(bool connected) =>
        SendAsync(connected
            ? "{\"op\":\"status\",\"voicemeeter\":{\"connected\":true,\"kind\":\"potato\",\"version\":\"3.1.1.2\"}}"
            : "{\"op\":\"status\",\"voicemeeter\":{\"connected\":false}}");

    /// <summary>Drops the connection without a close handshake — an agent restart.</summary>
    public void Abort()
    {
        Closed = true;
        _socket.Abort();
        _tcp.Dispose();
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var first = true;
        try
        {
            while (_socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await _socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                    break;
                }

                var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
                if (JsonNode.Parse(text) is not JsonObject frame)
                {
                    await SendAsync("{\"op\":\"error\",\"error\":\"invalid json\"}");
                    continue;
                }

                lock (Received)
                {
                    Received.Add(frame);
                }

                await _frames.Writer.WriteAsync(frame, ct);

                var op = frame["op"]?.GetValue<string>();
                if (first)
                {
                    first = false;
                    if (op != "hello")
                    {
                        await _socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "hello required", CancellationToken.None);
                        break;
                    }

                    if (frame["token"]?.GetValue<string>() != _server.Token)
                    {
                        await _socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "unauthorized", CancellationToken.None);
                        break;
                    }

                    await SendAsync(_server.VoicemeeterConnected
                        ? "{\"op\":\"welcome\",\"protocol\":1,\"voicemeeter\":{\"connected\":true,\"kind\":\"potato\",\"version\":\"3.1.1.2\"}}"
                        : "{\"op\":\"welcome\",\"protocol\":1,\"voicemeeter\":{\"connected\":false}}");
                    continue;
                }

                switch (op)
                {
                    case "watch":
                        await SendSnapshotAsync(frame);
                        break;
                    case "set" when _server.AutoReplySet:
                        var id = frame["id"]?.GetValue<string>();
                        var param = frame["param"]?.GetValue<string>() ?? "";
                        var value = frame["value"]?.GetValue<double>() ?? 0;
                        _server.Values[param] = value;
                        await SendResultAsync(id ?? "", true);
                        await SendChangedAsync(param, value);
                        break;
                    case "ping":
                        await SendAsync("{\"op\":\"pong\"}");
                        break;
                    default:
                        break;
                }
            }
        }
        finally
        {
            Closed = true;
            _frames.Writer.TryComplete();
        }
    }

    private async Task SendSnapshotAsync(JsonObject watch)
    {
        var values = new JsonObject();
        var errors = new JsonObject();
        if (watch["params"] is JsonArray names)
        {
            foreach (var node in names)
            {
                var name = node?.GetValue<string>() ?? "";
                if (_server.Values.TryGetValue(name, out var value))
                {
                    values[name] = value;
                }
                else
                {
                    errors[name] = "unknown parameter";
                }
            }
        }

        var frame = new JsonObject { ["op"] = "snapshot", ["values"] = values };
        if (errors.Count > 0)
        {
            frame["errors"] = errors;
        }

        await SendAsync(frame.ToJsonString());
    }
}
