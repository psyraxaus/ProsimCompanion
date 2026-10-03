using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Diagnostics;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.Notifications;
using ProsimCompanion.Core.State;
using Xunit;

namespace ProsimCompanion.Core.Tests.Notifications;

/// <summary>Issue #151: the three wire formats, the dispatcher's never-block / drop /
/// cooldown rules, the once-per-cycle signal source, and the secret handling.</summary>
public sealed class NotificationTests : IDisposable
{
    /// <summary>Records every request; answers with the scripted status, or hangs until cancelled.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(Uri Url, string? Auth, string ContentType, string Body, Dictionary<string, string> Headers)> Requests { get; } = [];
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public bool Hang { get; set; }
        public SemaphoreSlim Received { get; } = new(0);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var headers = request.Headers.Where(h => h.Key is "Title" or "Tags" or "Priority").ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.Ordinal);
            lock (Requests)
            {
                Requests.Add((request.RequestUri!, request.Headers.Authorization?.ToString(), request.Content?.Headers.ContentType?.MediaType ?? "", body, headers));
            }

            Received.Release();
            if (Hang)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return new HttpResponseMessage(Status) { ReasonPhrase = Status == HttpStatusCode.OK ? "OK" : Status.ToString() };
        }
    }

    private static readonly DateTimeOffset T0 = new(2026, 10, 3, 14, 5, 12, TimeSpan.Zero);
    private DateTimeOffset _now = T0;
    private readonly RecordingHandler _handler = new();
    private readonly NotificationOptions _options = new() { Enabled = true, SendTimeoutSeconds = 1, FailureCooldownSeconds = 60 };
    private readonly Core.EventLog.JsonlEventLog _eventLog = Speech.SpeechTestSupport.TempEventLog();
    private NotificationDispatcher? _dispatcher;

    private static NotificationTarget Target(string name, NotificationTargetKind kind, string url = "https://example.test/hook", string token = "")
        => new() { Name = name, Kind = kind, Url = url, Token = token };

    private static NotificationMessage Boarding() => new(
        FlightEvent.BoardingComplete, "Boarding complete", "All passengers are on board. 150 passengers aboard.",
        "BAW552", "EGLL–LIRF", T0, new Dictionary<string, object?> { ["paxCount"] = 150 });

    private NotificationDispatcher Dispatcher()
    {
        var monitor = new Mock<IOptionsMonitor<NotificationOptions>>();
        monitor.SetupGet(m => m.CurrentValue).Returns(() => _options);
        _dispatcher = new NotificationDispatcher(monitor.Object, _eventLog, NullLogger<NotificationDispatcher>.Instance, _handler, () => _now);
        _dispatcher.Start();
        return _dispatcher;
    }

    private async Task WaitForRequests(int count)
    {
        for (var i = 0; i < count; i++)
        {
            Assert.True(await _handler.Received.WaitAsync(TimeSpan.FromSeconds(5)), $"request {i + 1} of {count} never arrived");
        }
    }

    public void Dispose()
    {
        _dispatcher?.Dispose();
        _eventLog.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    // ---- formatters ------------------------------------------------------------------------

    [Fact]
    public void Webhook_IsTheVersionedJsonBody()
    {
        var wire = NotificationFormatters.Webhook(Boarding());
        var body = JsonNode.Parse(wire.Body)!;

        Assert.Equal("application/json", wire.ContentType);
        Assert.Equal("prosimcompanion.notification/1", (string?)body["schema"]);
        Assert.Equal("boarding-complete", (string?)body["event"]);
        Assert.Equal("Boarding complete", (string?)body["title"]);
        Assert.Equal("BAW552", (string?)body["flightNumber"]);
        Assert.Equal("EGLL–LIRF", (string?)body["route"]);
        Assert.Equal("2026-10-03T14:05:12Z", (string?)body["atUtc"]);
        Assert.Equal(150, (int?)body["details"]!["paxCount"]);
        Assert.Equal("ProsimCompanion", (string?)body["source"]);
        Assert.Empty(wire.Headers);
    }

    [Fact]
    public void Webhook_OmitsUnknownFlightAndRoute()
    {
        var body = JsonNode.Parse(NotificationFormatters.Webhook(Boarding() with { FlightNumber = "", Route = "", Details = null }).Body)!.AsObject();

        Assert.False(body.ContainsKey("flightNumber"));
        Assert.False(body.ContainsKey("route"));
        Assert.False(body.ContainsKey("details"));
    }

    [Fact]
    public void Ntfy_IsPlainTextWithHeaders()
    {
        var wire = NotificationFormatters.Ntfy(Boarding());

        Assert.Equal("text/plain", wire.ContentType);
        Assert.Equal("All passengers are on board. 150 passengers aboard.", wire.Body);
        Assert.Equal("BAW552 - Boarding complete", wire.Headers["Title"]);      // ASCII only in headers
        Assert.Equal("busts_in_silhouette", wire.Headers["Tags"]);
        Assert.Equal("default", wire.Headers["Priority"]);
        Assert.Equal("high", NotificationFormatters.Ntfy(Boarding() with { Event = FlightEvent.DeiceHoldoverExpiring }).Headers["Priority"]);
    }

    [Fact]
    public void Discord_IsAContentMessage()
    {
        var body = JsonNode.Parse(NotificationFormatters.Discord(Boarding()).Body)!;

        Assert.Equal("**BAW552 · Boarding complete** — All passengers are on board. 150 passengers aboard.", (string?)body["content"]);
    }

    // ---- the dispatcher ----------------------------------------------------------------------

    [Fact]
    public async Task Enqueue_SendsToEveryTargetThatWantsTheEvent_WithTheBearerHeader()
    {
        _options.Targets =
        [
            Target("Phone", NotificationTargetKind.Ntfy, "https://ntfy.test/topic", token: "tk-123"),
            Target("Discord", NotificationTargetKind.Discord, "https://discord.test/api/webhooks/1/x"),
            new NotificationTarget { Name = "Quiet", Kind = NotificationTargetKind.Webhook, Url = "https://quiet.test/", Events = new NotificationEventSwitches { BoardingComplete = false } },
        ];
        var dispatcher = Dispatcher();

        Assert.True(dispatcher.Enqueue(Boarding()));
        await WaitForRequests(2);

        var ntfy = Assert.Single(_handler.Requests, r => r.Url.Host == "ntfy.test");
        Assert.Equal("Bearer tk-123", ntfy.Auth);
        Assert.Equal("text/plain", ntfy.ContentType);
        var discord = Assert.Single(_handler.Requests, r => r.Url.Host == "discord.test");
        Assert.Null(discord.Auth);
        Assert.Contains("\"content\"", discord.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(_handler.Requests, r => r.Url.Host == "quiet.test");
        Assert.True(dispatcher.LastResults["Phone"].Ok);
        Assert.StartsWith("Sent · 200 OK", dispatcher.LastResults["Phone"].Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Enqueue_IsFalse_WhenOff_OrNoTargetWantsIt_AndNeverThrows()
    {
        var dispatcher = Dispatcher();
        Assert.False(dispatcher.Enqueue(Boarding()));                      // no targets

        _options.Targets = [Target("Phone", NotificationTargetKind.Ntfy)];
        _options.Enabled = false;
        Assert.False(dispatcher.Enqueue(Boarding()));                      // master off
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task AHungTarget_NeverBlocksTheRaiser_AndTimesOut()
    {
        _options.Targets = [Target("Slow", NotificationTargetKind.Webhook, "https://slow.test/")];
        _handler.Hang = true;
        var dispatcher = Dispatcher();

        var started = DateTime.UtcNow;
        Assert.True(dispatcher.Enqueue(Boarding()));
        var enqueueMs = (DateTime.UtcNow - started).TotalMilliseconds;
        Assert.True(enqueueMs < 200, $"Enqueue took {enqueueMs} ms — the raiser waited on the send");

        await WaitForRequests(1);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!dispatcher.LastResults.ContainsKey("Slow") && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        var result = Assert.Contains("Slow", (IDictionary<string, NotificationSendResult>)dispatcher.LastResults);
        Assert.False(result.Ok);
        Assert.Contains("timed out after 1 s", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedTarget_IsInCooldown_AndWakesAfterIt()
    {
        _options.Targets = [Target("Phone", NotificationTargetKind.Ntfy, "https://ntfy.test/t")];
        _handler.Status = HttpStatusCode.Unauthorized;
        var dispatcher = Dispatcher();

        dispatcher.Enqueue(Boarding());
        await WaitForRequests(1);
        await WaitFor(() => dispatcher.LastResults.ContainsKey("Phone"));
        Assert.Contains("HTTP 401", dispatcher.LastResults["Phone"].Summary, StringComparison.Ordinal);

        // Inside the cooldown: the next milestone is dropped for this target, no request.
        _handler.Status = HttpStatusCode.OK;
        _now = T0.AddSeconds(30);
        dispatcher.Enqueue(Boarding() with { Event = FlightEvent.CabinSecure });
        await Task.Delay(300);
        Assert.Single(_handler.Requests);

        // After it: sends again and the cooldown clears on success.
        _now = T0.AddSeconds(61);
        dispatcher.Enqueue(Boarding() with { Event = FlightEvent.Landed });
        await WaitForRequests(1);
        await WaitFor(() => dispatcher.LastResults["Phone"].Ok);
        Assert.Equal(2, _handler.Requests.Count);
    }

    [Fact]
    public async Task AFullQueue_DropsTheNewest_AndCounts()
    {
        _options.Targets = [Target("Slow", NotificationTargetKind.Webhook, "https://slow.test/")];
        _handler.Hang = true;
        var dispatcher = Dispatcher();

        // The first message is taken by the sender and hangs there (wait for its request so
        // the channel is empty again); the next QueueCapacity fill the queue; one more drops.
        Assert.True(dispatcher.Enqueue(Boarding()));
        await WaitForRequests(1);
        for (var i = 0; i < NotificationDispatcher.QueueCapacity; i++)
        {
            Assert.True(dispatcher.Enqueue(Boarding()), $"message {i} should have queued");
        }

        Assert.False(dispatcher.Enqueue(Boarding()));
        Assert.Equal(1, dispatcher.Dropped);
    }

    [Fact]
    public async Task SendTest_IgnoresTheMasterSwitch_AndReportsOneLine()
    {
        _options.Enabled = false;
        var dispatcher = Dispatcher();

        var result = await dispatcher.SendTestAsync(Target("Phone", NotificationTargetKind.Ntfy, "https://ntfy.test/t"), "BAW552", "EGLL–LIRF");

        Assert.True(result.Ok);
        Assert.Equal(200, result.StatusCode);
        var request = Assert.Single(_handler.Requests);
        Assert.Equal("BAW552 - Test notification", request.Headers["Title"]);
        Assert.Contains("can reach this target", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnInvalidUrl_FailsWithoutARequest()
    {
        var dispatcher = Dispatcher();

        var result = await dispatcher.SendTestAsync(Target("Bad", NotificationTargetKind.Webhook, "not a url"), null, null);

        Assert.False(result.Ok);
        Assert.Contains("not a valid http(s) address", result.Summary, StringComparison.Ordinal);
        Assert.Empty(_handler.Requests);
    }

    // ---- the signal source -------------------------------------------------------------------

    private sealed class Harness
    {
        public GroundOpsSignals Signals { get; } = new();
        public LoadsheetStore Loadsheet { get; } = new();
        public FlightTimesStore Times { get; } = new();
        public OfpStore Ofp { get; } = new();
        public List<NotificationMessage> Queued { get; } = [];
        public NotificationSignalSource Source { get; }

        public Harness(NotificationTests owner)
        {
            owner._options.Targets = [Target("Phone", NotificationTargetKind.Ntfy, "https://ntfy.test/t")];
            var dispatcher = owner.Dispatcher();
            Ofp.Set(new OfpData { Callsign = "BAW552", OriginIcao = "EGLL", DestinationIcao = "LIRF", PaxCount = 150 });
            var monitor = new Mock<IOptionsMonitor<NotificationOptions>>();
            monitor.SetupGet(m => m.CurrentValue).Returns(() => owner._options);
            Source = new NotificationSignalSource(dispatcher, Signals, Loadsheet, Times, Ofp, monitor.Object,
                NullLogger<NotificationSignalSource>.Instance, clock: () => owner._now);
            Source.Start();
        }
    }

    [Fact]
    public async Task Milestones_FireOncePerCycle_AndReArmOnReset()
    {
        var h = new Harness(this);

        h.Signals.RaiseBoardingCompleted();
        h.Signals.RaiseBoardingCompleted();                    // a GSX re-sync: swallowed
        h.Signals.RaiseDepartureServicesCompleted();
        await WaitForRequests(2);
        await Task.Delay(200);
        Assert.Equal(2, _handler.Requests.Count);
        Assert.Contains(_handler.Requests, r => r.Headers["Title"] == "BAW552 - Boarding complete" && r.Body.Contains("150 passengers", StringComparison.Ordinal));
        Assert.Contains(_handler.Requests, r => r.Headers["Title"] == "BAW552 - Ready for pushback");

        h.Signals.RaiseFlightCycleReset();
        h.Signals.RaiseBoardingCompleted();
        await WaitForRequests(1);
        Assert.Equal(3, _handler.Requests.Count);
    }

    [Fact]
    public async Task FinalLoadsheet_FiresPerEdition_AndTimesFireOnTheirEdges()
    {
        var h = new Harness(this);

        h.Loadsheet.SetFinal(new LoadsheetSlotView(LoadsheetSlotStatus.Sent, 1, T0, 58800, 66900, 28, 27, 8100, 148, null));
        h.Loadsheet.SetFinal(new LoadsheetSlotView(LoadsheetSlotStatus.Sent, 1, T0, 58800, 66900, 28, 27, 8100, 148, null));   // same edition
        h.Loadsheet.SetFinal(new LoadsheetSlotView(LoadsheetSlotStatus.Sent, 2, T0, 59000, 67100, 28, 27, 8100, 149, null));   // revised
        h.Times.Update(_ => new FlightTimesSnapshot(T0.AddHours(-2), T0.AddHours(-1.8), T0, null));
        h.Times.Update(t => t with { OnBlocksUtc = T0.AddMinutes(8) });
        await WaitForRequests(4);
        await Task.Delay(200);

        var titles = _handler.Requests.Select(r => r.Headers["Title"]).ToList();
        Assert.Equal(2, titles.Count(t => t == "BAW552 - Final loadsheet sent"));
        Assert.Single(titles, t => t == "BAW552 - Landed");
        Assert.Single(titles, t => t == "BAW552 - On blocks");
        Assert.Contains(_handler.Requests, r => r.Body.Contains("edition 2", StringComparison.Ordinal) && r.Body.Contains("149 passengers", StringComparison.Ordinal));
        Assert.Contains(_handler.Requests, r => r.Body.Contains("Block time 2h 08m", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TopOfDescent_CarriesTheEstimate()
    {
        var h = new Harness(this);

        h.Signals.RaiseTodApproaching(new TodApproaching(9.6, 72, 180, 108, 37000, 15, 450, 10));
        await WaitForRequests(1);

        var request = Assert.Single(_handler.Requests);
        Assert.Equal("BAW552 - Top of descent approaching", request.Headers["Title"]);
        Assert.Contains("About 10 minutes to the top of descent", request.Body, StringComparison.Ordinal);
    }

    // ---- secrets -----------------------------------------------------------------------------

    [Fact]
    public void TargetUrlAndToken_AreRegisteredSecrets_ProtectedPerListEntry()
    {
        Assert.Contains("notifications:targets:*:url", SecretProtector.SecretPaths);
        Assert.Contains("notifications:targets:*:token", SecretProtector.SecretPaths);

        var root = (JsonObject)JsonNode.Parse("""
            { "notifications": { "enabled": true, "targets": [
                { "name": "Phone", "kind": "ntfy", "url": "https://ntfy.sh/secret-topic", "token": "tk-1" },
                { "name": "HA", "kind": "webhook", "url": "https://ha.local/hook", "token": "" } ] } }
            """)!;

        Assert.True(SecretProtector.ProtectKnownSecrets(root));
        var targets = root["notifications"]!["targets"]!.AsArray();
        Assert.StartsWith("dpapi:", (string?)targets[0]!["url"], StringComparison.Ordinal);
        Assert.StartsWith("dpapi:", (string?)targets[0]!["token"], StringComparison.Ordinal);
        Assert.StartsWith("dpapi:", (string?)targets[1]!["url"], StringComparison.Ordinal);
        Assert.Equal("", (string?)targets[1]!["token"]);                   // empty stays empty
        Assert.Equal("Phone", (string?)targets[0]!["name"]);
        Assert.False(SecretProtector.ProtectKnownSecrets(root));            // second pass: nothing left to protect

        var keys = SecretProtector.ExpandSecretKeys(["notifications:targets:0:url", "notifications:targets:1:token", "notifications:enabled", "webUi:accessToken"]).ToList();
        Assert.Contains("notifications:targets:0:url", keys);
        Assert.Contains("notifications:targets:1:token", keys);
        Assert.Contains("webUi:accessToken", keys);
        Assert.DoesNotContain("notifications:enabled", keys);
        Assert.DoesNotContain(keys, k => k.Contains('*', StringComparison.Ordinal));
    }

    [Fact]
    public void Bundle_RedactsTargetUrlsAndTokens_KeepsNamesAndKinds()
    {
        var redacted = JsonNode.Parse(SettingsRedactor.Redact("""
            { "notifications": { "enabled": true, "targets": [ { "name": "Phone", "kind": "ntfy", "url": "https://ntfy.sh/t", "token": "tk" } ] },
              "briefing": { "llmBaseUrl": "http://llm.local:11434" } }
            """))!;

        var target = redacted["notifications"]!["targets"]![0]!;
        Assert.Equal(SettingsRedactor.Mask, (string?)target["url"]);
        Assert.Equal(SettingsRedactor.Mask, (string?)target["token"]);
        Assert.Equal("Phone", (string?)target["name"]);
        Assert.Equal("ntfy", (string?)target["kind"]);
        Assert.Equal("http://llm.local:11434", (string?)redacted["briefing"]!["llmBaseUrl"]);   // service URLs stay readable
    }

    [Fact]
    public async Task LogAndSessionEvents_CarryTheTargetNameAndStatus_NeverTheUrlOrToken()
    {
        _options.Targets = [Target("Phone", NotificationTargetKind.Ntfy, "https://ntfy.test/very-secret-topic", token: "tk-secret")];
        _handler.Status = HttpStatusCode.Forbidden;
        var dispatcher = Dispatcher();
        dispatcher.Enqueue(Boarding());
        await WaitForRequests(1);
        await WaitFor(() => dispatcher.LastResults.ContainsKey("Phone"));
        await _eventLog.DisposeAsync();

        var log = File.ReadAllText(_eventLog.Path);
        Assert.Contains("\"notify.failed\"", log, StringComparison.Ordinal);
        Assert.Contains("\"target\":\"Phone\"", log, StringComparison.Ordinal);
        Assert.Contains("\"status\":403", log, StringComparison.Ordinal);
        Assert.DoesNotContain("very-secret-topic", log, StringComparison.Ordinal);
        Assert.DoesNotContain("tk-secret", log, StringComparison.Ordinal);
        Assert.DoesNotContain("very-secret-topic", dispatcher.LastResults["Phone"].Summary, StringComparison.Ordinal);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        Assert.True(condition(), "condition not met in time");
    }
}
