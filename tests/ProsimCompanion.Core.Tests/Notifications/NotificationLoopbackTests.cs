using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Notifications;
using Xunit;

namespace ProsimCompanion.Core.Tests.Notifications;

/// <summary>Issue #151, the real socket path: the dispatcher's own HttpClient against a local
/// HTTP listener — the production handler, a real POST, the real headers.</summary>
public sealed class NotificationLoopbackTests
{
    [Fact]
    public async Task SendTest_ReachesALocalWebhook_WithBodyAndBearer()
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/hook/");
        listener.Start();
        var received = listener.GetContextAsync();

        var options = new NotificationOptions { Enabled = true, SendTimeoutSeconds = 5 };
        var monitor = new Mock<IOptionsMonitor<NotificationOptions>>();
        monitor.SetupGet(m => m.CurrentValue).Returns(options);
        await using var eventLog = Speech.SpeechTestSupport.TempEventLog();
        using var dispatcher = new NotificationDispatcher(monitor.Object, eventLog, NullLogger<NotificationDispatcher>.Instance);
        var target = new NotificationTarget { Name = "Local", Kind = NotificationTargetKind.Webhook, Url = $"http://127.0.0.1:{port}/hook/", Token = "secret-token" };

        var sendTask = dispatcher.SendTestAsync(target, "BAW552", "EGLL–LIRF");
        var context = await received.WaitAsync(TimeSpan.FromSeconds(5));
        using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        context.Response.StatusCode = 200;
        context.Response.Close();
        var result = await sendTask;

        Assert.True(result.Ok, result.Summary);
        Assert.Equal("POST", context.Request.HttpMethod);
        Assert.Equal("Bearer secret-token", context.Request.Headers["Authorization"]);
        Assert.StartsWith("application/json", context.Request.ContentType, StringComparison.Ordinal);
        var json = JsonNode.Parse(body)!;
        Assert.Equal("test", (string?)json["event"]);
        Assert.Equal("BAW552", (string?)json["flightNumber"]);
        Assert.Equal("prosimcompanion.notification/1", (string?)json["schema"]);
    }

    private static int FreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
