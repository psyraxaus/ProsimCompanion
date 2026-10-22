using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Audio.Mixer;

/// <summary>
/// The Test-button probe and the socket helpers the per-agent client shares with it: a
/// throwaway connection that sends <c>hello</c> and reads one frame. No session state, no
/// store — it works off unsaved values and never touches the running clients.
/// </summary>
public static class MixerProbe
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    public static async Task<MixerProbeResult> ProbeAsync(string host, int port, string token, CancellationToken cancellationToken = default)
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
            var hello = Encoding.UTF8.GetBytes(MixerFrame.BuildHello(token ?? ""));
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

    internal static Uri BuildUri(string host, int port) => new($"ws://{host.Trim()}:{port}/ws");

    internal static async Task<(string Text, string? Close)> ReceiveOneAsync(ClientWebSocket socket, CancellationToken cancellationToken)
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

    internal static async Task CloseQuietlyAsync(ClientWebSocket socket)
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
}
