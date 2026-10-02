using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Debrief;
using ProsimCompanion.Core.Logbook;
using ProsimCompanion.Core.Sessions;
using ProsimCompanion.Core.Tests.Debrief;
using ProsimCompanion.Core.Tests.TechLog;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Debrief;
using ProsimCompanion.Speech.Llm;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>
/// The debrief on the streamed path (issue #147): spoken sentence by sentence at Low with
/// the same shelf life and validity window, the logbook comparison line always last and never
/// paraphrased, the spoken text persisted, and the whole-reply path untouched when the
/// switch is off.
/// </summary>
public sealed class DebriefStreamingTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pc-debrief-stream-").FullName;
    private readonly DebriefOptions _options = new();
    private readonly BriefingOptions _briefing = new()
    {
        LlmEnabled = true,
        LlmModel = "test-model",
        LlmBaseUrl = "http://llm.invalid/api",
        LlmTimeoutSeconds = 0,
    };

    private readonly FakePhaseSource _phases = new();
    private readonly DrainingArbiter _arbiter = new();

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

    private async Task<(DebriefService Service, string SessionPath)> CreateAsync(HttpMessageHandler handler, bool withNarrator)
    {
        var eventLog = new Core.EventLog.JsonlEventLog(_dir, NullLogger<Core.EventLog.JsonlEventLog>.Instance);
        await eventLog.DisposeAsync();
        var sessionPath = eventLog.Path;
        new SessionLogBuilder()
            .At("10:00:00").Event("callout.fired", new { id = "a" })
            .At("10:01:00").Event("callout.fired", new { id = "b" })
            .Write(_dir, Path.GetFileNameWithoutExtension(sessionPath));

        var logbook = new Mock<ILogbookService>();
        logbook.Setup(l => l.DescribeComparison(It.IsAny<DebriefFacts>(), It.IsAny<string>()))
            .Returns("That's landing number 3 into YMML.");
        var monitor = OptionsSupport.Monitor(_briefing);
        var llm = new OpenAiChatClient(monitor, new HttpClient(handler)) { StreamTimeoutFloorSeconds = 0.3 };
        var narrator = withNarrator
            ? new StreamingNarrator(llm, _arbiter, eventLog, monitor, NullLogger<StreamingNarrator>.Instance)
            : null;
        var service = new DebriefService(
            new DebriefFactExtractor(NullLogger<DebriefFactExtractor>.Instance),
            logbook.Object,
            _arbiter,
            _phases,
            eventLog,
            OptionsSupport.Monitor(_options),
            monitor,
            NullLogger<DebriefService>.Instance,
            new ProsimCompanion.Core.Speech.SpokenText(),
            llm,
            narrator: narrator);
        service.Start();
        return (service, sessionPath);
    }

    private static Task RunStepAsync(DebriefService service, string sessionPath)
        => ((ISessionFinalizationStep)service).RunAsync(
            new SessionFinalizationContext(sessionPath, Path.GetFileNameWithoutExtension(sessionPath)),
            CancellationToken.None);

    private async Task WaitForSegments(int count)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (_arbiter.Segments.Count < count)
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, $"timed out waiting for {count} segment(s)");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Streamed_SpeaksSentenceBySentenceAtLow_ThenTheLogbookLine_AndPersistsWhatWasSaid()
    {
        var handler = new StreamHandler(() => LlmWire.Sliced(
            LlmWire.Sse("Nice flight, Captain. ", "Two callouts, all clean. Good work today."), 9));
        var (service, sessionPath) = await CreateAsync(handler, withNarrator: true);

        await RunStepAsync(service, sessionPath);   // returns at once: the narration runs in the background
        await WaitForSegments(4);

        var request = Assert.Single(_arbiter.Requests);
        Assert.NotNull(request.Stream);
        Assert.Equal(SpeechPriority.Low, request.Priority);
        Assert.Equal("debrief", request.Tag);
        Assert.Equal(TimeSpan.FromMinutes(10), request.Ttl);
        Assert.NotNull(request.IsStillValid);
        Assert.Equal(
            ["Nice flight, Captain.", "Two callouts, all clean.", "Good work today.", "That's landing number 3 into YMML."],
            _arbiter.Segments.Select(s => s.Text));
        Assert.Equal([false, false, false, true], _arbiter.Segments.Select(s => s.Cacheable));

        var debriefPath = Path.ChangeExtension(sessionPath, ".debrief.txt");
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (!File.Exists(debriefPath) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.Equal(
            "Nice flight, Captain. Two callouts, all clean. Good work today. That's landing number 3 into YMML.",
            File.ReadAllText(debriefPath));
    }

    [Fact]
    public async Task Streamed_AnInventedNumber_IsCutOff_AndTheTemplateFinishes_WithTheLogbookLineLast()
    {
        var handler = new StreamHandler(() => LlmWire.Whole(
            LlmWire.Sse("Nice flight, Captain. We were airborne three hundred and twenty minutes. Good work.")));
        var (service, sessionPath) = await CreateAsync(handler, withNarrator: true);

        await RunStepAsync(service, sessionPath);
        await WaitForSegments(4);

        Assert.Equal(
            ["Nice flight, Captain.", "2 callouts made.", "Good flight.", "That's landing number 3 into YMML."],
            _arbiter.Segments.Select(s => s.Text));
    }

    [Fact]
    public async Task SwitchOff_KeepsTheWholeReplyPath()
    {
        _briefing.StreamLlm = false;
        var handler = new StreamHandler(() => LlmWire.Whole(LlmWire.Sse("unused")));
        var (service, sessionPath) = await CreateAsync(handler, withNarrator: true);

        await RunStepAsync(service, sessionPath);

        // The whole-reply client asks for a non-streaming completion; this stub cannot answer
        // one, so the template is spoken as ONE ordinary utterance — no stream in sight.
        var request = Assert.Single(_arbiter.Requests);
        Assert.Null(request.Stream);
        Assert.Equal("Debrief. 2 callouts made. Good flight. That's landing number 3 into YMML.", request.Text);
        Assert.Empty(_arbiter.Segments);
    }
}
