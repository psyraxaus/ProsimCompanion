using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Debrief;
using ProsimCompanion.Core.Logbook;
using ProsimCompanion.Core.Sessions;
using ProsimCompanion.Core.Tests.Debrief;
using ProsimCompanion.Core.Tests.TechLog;
using ProsimCompanion.Speech.Debrief;
using ProsimCompanion.Speech.Llm;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>
/// The debrief LLM styling path end-to-end through <see cref="DebriefService"/> with a fake
/// HTTP handler standing in for the OpenAI-compatible endpoint: verified output is spoken,
/// unverified output gets exactly ONE strict re-ask and then the deterministic template, and a
/// closed gate (either the debrief's UseLlm or the briefing's LlmEnabled) never spends a call.
/// </summary>
public sealed class DebriefLlmStylingTests : IDisposable
{
    /// <summary>Deterministic template for the seeded session (2 callout events + sign-off).</summary>
    private const string Template = "Debrief. 2 callouts made. Good flight.";

    private readonly string _dir = Directory.CreateTempSubdirectory("pc-debrief-llm-").FullName;
    private readonly DebriefOptions _options = new();
    private readonly BriefingOptions _briefing = new()
    {
        LlmEnabled = true,
        LlmModel = "test-model",
        LlmBaseUrl = "http://llm.invalid/api", // never resolved — the fake handler answers
    };

    private readonly FakePhaseSource _phases = new();
    private readonly FakeArbiter _arbiter = new();
    private readonly FakeLlmHandler _handler = new();

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // best effort — the event-log writer may still hold its file
        }
    }

    /// <summary>Scripted chat-completions endpoint: dequeues one assistant reply per request
    /// and records every user-message body for prompt assertions.</summary>
    private sealed class FakeLlmHandler : HttpMessageHandler
    {
        public Queue<string> Replies { get; } = new();
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            var content = Replies.Dequeue();
            var json = JsonSerializer.Serialize(new
            {
                choices = new[] { new { message = new { role = "assistant", content } } },
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }
    }

    private async Task<(DebriefService Service, string SessionPath)> CreateAsync()
    {
        var eventLog = new Core.EventLog.JsonlEventLog(_dir, NullLogger<Core.EventLog.JsonlEventLog>.Instance);
        await eventLog.DisposeAsync();
        var sessionPath = eventLog.Path;
        new SessionLogBuilder()
            .At("10:00:00").Event("callout.fired", new { id = "a" })
            .At("10:01:00").Event("callout.fired", new { id = "b" })
            .Write(_dir, Path.GetFileNameWithoutExtension(sessionPath));

        var service = new DebriefService(
            new DebriefFactExtractor(NullLogger<DebriefFactExtractor>.Instance),
            new Mock<ILogbookService>().Object,
            _arbiter,
            _phases,
            eventLog,
            OptionsSupport.Monitor(_options),
            OptionsSupport.Monitor(_briefing),
            NullLogger<DebriefService>.Instance,
            new OpenAiChatClient(OptionsSupport.Monitor(_briefing), new HttpClient(_handler)));
        service.Start();
        return (service, sessionPath);
    }

    private static Task RunStepAsync(DebriefService service, string sessionPath)
        => ((ISessionFinalizationStep)service).RunAsync(
            new SessionFinalizationContext(sessionPath, Path.GetFileNameWithoutExtension(sessionPath)),
            CancellationToken.None);

    [Fact]
    public async Task VerifiedLlmOutput_IsSpoken()
    {
        _handler.Replies.Enqueue("Nice flight, Captain — 2 callouts, all clean. Good work today.");
        var (service, sessionPath) = await CreateAsync();

        await RunStepAsync(service, sessionPath);

        var request = Assert.Single(_arbiter.Requests);
        Assert.Equal("Nice flight, Captain — 2 callouts, all clean. Good work today.", request.Text);
        Assert.Single(_handler.RequestBodies); // no re-ask when the numbers check out
        Assert.Contains("POST-FLIGHT FACTS:", _handler.RequestBodies[0], StringComparison.Ordinal);
        Assert.Contains("Callouts made: 2", _handler.RequestBodies[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnverifiedTwice_StrictReAsk_ThenTemplate()
    {
        _handler.Replies.Enqueue("We were airborne 320 minutes.");   // invented number
        _handler.Replies.Enqueue("Still claiming 320 minutes.");     // re-ask fails too
        var (service, sessionPath) = await CreateAsync();

        await RunStepAsync(service, sessionPath);

        Assert.Equal(Template, Assert.Single(_arbiter.Requests).Text);
        Assert.Equal(2, _handler.RequestBodies.Count); // exactly ONE re-ask
        Assert.Contains("Use ONLY these numbers, exactly as written, and no others:",
            _handler.RequestBodies[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnverifiedOnce_ReAskPasses_ReAskTextIsSpoken()
    {
        _handler.Replies.Enqueue("We were airborne 320 minutes.");
        _handler.Replies.Enqueue("A clean flight with 2 callouts. Well flown.");
        var (service, sessionPath) = await CreateAsync();

        await RunStepAsync(service, sessionPath);

        Assert.Equal("A clean flight with 2 callouts. Well flown.", Assert.Single(_arbiter.Requests).Text);
    }

    [Fact]
    public async Task LlmHttpFailure_FallsBackToTemplate()
    {
        // An empty reply queue makes the fake handler throw — any exception must land on the
        // template, never on silence or an unhandled fault.
        var (service, sessionPath) = await CreateAsync();

        await RunStepAsync(service, sessionPath);

        Assert.Equal(Template, Assert.Single(_arbiter.Requests).Text);
    }

    [Fact]
    public async Task UseLlmOff_SpeaksTemplate_WithoutSpendingACall()
    {
        _options.UseLlm = false;
        _handler.Replies.Enqueue("never used");
        var (service, sessionPath) = await CreateAsync();

        await RunStepAsync(service, sessionPath);

        Assert.Equal(Template, Assert.Single(_arbiter.Requests).Text);
        Assert.Empty(_handler.RequestBodies);
    }

    [Fact]
    public async Task LlmNotConfigured_SpeaksTemplate_WithoutSpendingACall()
    {
        _briefing.LlmEnabled = false;
        _handler.Replies.Enqueue("never used");
        var (service, sessionPath) = await CreateAsync();

        await RunStepAsync(service, sessionPath);

        Assert.Equal(Template, Assert.Single(_arbiter.Requests).Text);
        Assert.Empty(_handler.RequestBodies);
    }
}
