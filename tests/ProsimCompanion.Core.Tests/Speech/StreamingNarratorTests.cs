using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Speech;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Llm;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>
/// Issue #147 end to end below the services: a stub chat endpoint streams text, the narrator
/// cuts, verifies and hands sentences to an arbiter that speaks them — and the template is
/// the floor whenever the model fails, stalls, times out or is cut off.
/// </summary>
public sealed class StreamingNarratorTests
{
    private static readonly NarrationSection[] Sections =
    [
        new("header", "Departure briefing.") { IsOpening = true },
        new("runway", "Runway 16R.") { Runway = "16R" },
        new("track", "Initial track 163 degrees.") { Numbers = [163], AnyOf = ["track", "heading"] },
        new("wind", "Wind 250 at 14 knots.") { Numbers = [250, 14], AnyOf = ["wind"] },
        new("qnh", "QNH 1013.") { Numbers = [1013], AnyOf = ["qnh"] },
    ];

    private const string Template = "Departure briefing. Runway 16R. Initial track 163 degrees. Wind 250 at 14 knots. QNH 1013.";

    private static readonly double[] Allowed = [16, 163, 250, 14, 1013];

    private readonly DrainingArbiter _arbiter = new();
    private readonly BriefingOptions _options = new()
    {
        LlmEnabled = true,
        LlmBaseUrl = "http://llm.test/v1",
        LlmModel = "test-model",
        LlmTimeoutSeconds = 0,
    };

    private StreamingNarrator Narrator(
        HttpMessageHandler handler, LlmHealthStore? health = null, double timeoutFloorSeconds = 5, TimeSpan? stallAfter = null)
    {
        var monitor = SpeechTestSupport.BriefingMonitor(_options);
        var client = new OpenAiChatClient(monitor, new HttpClient(handler), health)
        {
            StreamTimeoutFloorSeconds = timeoutFloorSeconds,
        };
        return new StreamingNarrator(
            client, _arbiter, SpeechTestSupport.TempEventLog(), monitor, NullLogger<StreamingNarrator>.Instance)
        {
            StallAfter = stallAfter ?? TimeSpan.FromSeconds(4),
            WatchInterval = TimeSpan.FromMilliseconds(20),
        };
    }

    private static NarrationPlan Plan(params string[] epilogue) => new(
        "briefing.departure", "system", "facts", Allowed, Sections,
        new SpeechRequest("Departure briefing", SpeechPriority.Normal, Tag: "briefing.departure"),
        epilogue);

    private static StreamHandler Sse(params string[] deltas) => new(() => LlmWire.Sliced(LlmWire.Sse(deltas), 11));

    private string[] SegmentTexts => [.. _arbiter.Segments.Select(s => s.Text)];

    [Fact]
    public async Task VerifiedSentences_AreSpokenInOrder_AsOneStreamedItem_OutsideTheCache()
    {
        var narrator = Narrator(Sse(
            "Good morning Captain. We depart run", "way one six right. Initial track one six three degrees. ",
            "Wind two five zero at one four knots, Q N H one zero one three."));

        var result = await narrator.RunAsync(Plan());

        Assert.Equal(
            [
                "Good morning Captain.",
                "We depart runway one six right.",
                "Initial track one six three degrees.",
                "Wind two five zero at one four knots, Q N H one zero one three.",
            ],
            SegmentTexts);
        // One queue item: nothing at the same priority can get between its sentences.
        var request = Assert.Single(_arbiter.Requests);
        Assert.NotNull(request.Stream);
        Assert.Equal(SpeechPriority.Normal, request.Priority);
        Assert.Equal("briefing.departure", request.Tag);
        // LLM sentences are one-off text: never read from or written to the TTS cache.
        Assert.All(_arbiter.Segments, s => Assert.False(s.Cacheable));

        Assert.Equal(SpeechOutcome.Spoken, result.Outcome);
        Assert.Equal(4, result.LlmSentences);
        Assert.Equal(TakeoverReason.None, result.Takeover);
        Assert.Equal(0, result.TemplateSections);
        Assert.False(result.Preempted);
        Assert.NotNull(result.FirstTokenMs);
        Assert.NotNull(result.FirstAudioMs);
        Assert.Equal(string.Join(" ", SegmentTexts), result.Text);
    }

    [Fact]
    public async Task VerifyFailMidStream_DropsThatSentenceAndTheRest_AndTheTemplateFinishes_WithoutRestarting()
    {
        var narrator = Narrator(Sse(
            "Good morning Captain. We depart runway one six right. ",
            "Initial track one six eight degrees. ",
            "Wind two five zero at one four knots. This sentence must never be heard."));

        var result = await narrator.RunAsync(Plan());

        Assert.Equal(
            [
                "Good morning Captain.",
                "We depart runway one six right.",
                // takeover: the sections not heard yet, in template order — no header, no runway
                "Initial track 163 degrees.",
                "Wind 250 at 14 knots.",
                "QNH 1013.",
            ],
            SegmentTexts);
        Assert.Single(_arbiter.Requests);
        Assert.DoesNotContain(SegmentTexts, s => s.Contains("eight", StringComparison.Ordinal));
        Assert.DoesNotContain(SegmentTexts, s => s.Contains("never be heard", StringComparison.Ordinal));
        // Model sentences bypass the cache; the template's own sentences use it.
        Assert.Equal([false, false, true, true, true], _arbiter.Segments.Select(s => s.Cacheable));

        Assert.Equal(TakeoverReason.VerifyFailed, result.Takeover);
        Assert.Equal(2, result.LlmSentences);
        Assert.Equal(3, result.TemplateSections);
        Assert.Equal(SpeechOutcome.Spoken, result.Outcome);
    }

    [Fact]
    public async Task VerifyFailOnTheVeryFirstSentence_SpeaksThePlainTemplate_AsOneOrdinaryUtterance()
    {
        var narrator = Narrator(Sse("Initial track one six eight degrees. Wind two five zero at one four knots."));

        var result = await narrator.RunAsync(Plan("Leg 2 of 4 complete."));

        var request = Assert.Single(_arbiter.Requests);
        Assert.Null(request.Stream);
        Assert.Equal(Template + " Leg 2 of 4 complete.", request.Text);
        Assert.Equal("briefing.departure", request.Tag);
        Assert.Empty(_arbiter.Segments);
        Assert.Equal(TakeoverReason.VerifyFailed, result.Takeover);
        Assert.Equal(0, result.LlmSentences);
        Assert.Equal(Sections.Length, result.TemplateSections);
        Assert.Equal(request.Text, result.Text);
    }

    [Fact]
    public async Task TimeoutBeforeTheFirstToken_FallsBackToTheTemplate_AndMarksTheEndpointUnreachable()
    {
        var health = new LlmHealthStore();
        var handler = new StreamHandler(() => new ScriptedStream([], ScriptedStream.Ending.Hang));

        var result = await Narrator(handler, health, timeoutFloorSeconds: 0.2).RunAsync(Plan());

        var request = Assert.Single(_arbiter.Requests);
        Assert.Null(request.Stream);
        Assert.Equal(Template, request.Text);
        Assert.Equal(TakeoverReason.Timeout, result.Takeover);
        Assert.Null(result.FirstTokenMs);
        Assert.Null(result.FirstAudioMs);
        Assert.Equal(LlmHealthState.Unreachable, health.Snapshot().State);
    }

    [Fact]
    public async Task AnEndpointError_FallsBackToTheTemplate()
    {
        var handler = new StreamHandler(() => LlmWire.Whole(""), System.Net.HttpStatusCode.InternalServerError);

        var result = await Narrator(handler).RunAsync(Plan());

        Assert.Equal(Template, Assert.Single(_arbiter.Requests).Text);
        Assert.Equal(TakeoverReason.Error, result.Takeover);
    }

    [Fact]
    public async Task AnEmptyReply_FallsBackToTheTemplate()
    {
        var result = await Narrator(new StreamHandler(() => LlmWire.Whole("data: [DONE]\n\n"))).RunAsync(Plan());

        Assert.Equal(Template, Assert.Single(_arbiter.Requests).Text);
        Assert.Equal(TakeoverReason.Error, result.Takeover);
    }

    [Fact]
    public async Task AStreamThatBreaksMidWay_IsFinishedByTheTemplate()
    {
        var head = Encoding.UTF8.GetBytes(LlmWire.Sse("Good morning Captain. We depart runway one six right. Initial tr")
            .Replace("data: [DONE]\n\n", "", StringComparison.Ordinal));
        var handler = new StreamHandler(() => new ScriptedStream([head], ScriptedStream.Ending.Break));

        var result = await Narrator(handler).RunAsync(Plan());

        Assert.Equal(
            ["Good morning Captain.", "We depart runway one six right.", "Initial track 163 degrees.", "Wind 250 at 14 knots.", "QNH 1013."],
            SegmentTexts);
        Assert.Equal(TakeoverReason.Error, result.Takeover);
    }

    [Fact]
    public async Task AModelThatStalls_WhileTheSpeechHasRunDry_IsFinishedByTheTemplate()
    {
        // Two sentences arrive, then the model goes quiet without closing the stream. The
        // idle budget is long (5 s); the stall rule is what must end the wait.
        var head = Encoding.UTF8.GetBytes(LlmWire.Sse("Good morning Captain. We depart runway one six right. Initial tr")
            .Replace("data: [DONE]\n\n", "", StringComparison.Ordinal));
        var handler = new StreamHandler(() => new ScriptedStream([head], ScriptedStream.Ending.Hang));
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var result = await Narrator(handler, stallAfter: TimeSpan.FromMilliseconds(150)).RunAsync(Plan());

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(4), "the stall rule, not the idle timeout, must end the wait");
        Assert.Equal(TakeoverReason.Stalled, result.Takeover);
        Assert.Equal(
            ["Good morning Captain.", "We depart runway one six right.", "Initial track 163 degrees.", "Wind 250 at 14 knots.", "QNH 1013."],
            SegmentTexts);
    }

    [Fact]
    public async Task ASlowModel_IsNotAStall_WhileTheSpeechStillHasSomethingToSay()
    {
        // Speaking takes longer than the stall limit, so the speech never runs dry while the
        // (slow) model delivers: no takeover.
        _arbiter.SegmentDuration = TimeSpan.FromMilliseconds(120);
        var body = LlmWire.Sse("Good morning Captain. We depart runway one six right. ", "Initial track one six three degrees. ", "Q N H one zero one three.");
        var handler = new StreamHandler(() => new ScriptedStream(
            Encoding.UTF8.GetBytes(body).Chunk(60), delay: TimeSpan.FromMilliseconds(15)));

        var result = await Narrator(handler, stallAfter: TimeSpan.FromMilliseconds(100)).RunAsync(Plan());

        Assert.Equal(TakeoverReason.None, result.Takeover);
        Assert.Equal(4, result.LlmSentences);
    }

    [Fact]
    public async Task TheEpilogue_IsAlwaysSpokenLast_AndIsCacheable()
    {
        var narrator = Narrator(Sse("Good morning Captain. We depart runway one six right."));

        await narrator.RunAsync(Plan("That's landing number 3 into YMML."));

        Assert.Equal("That's landing number 3 into YMML.", SegmentTexts[^1]);
        Assert.True(_arbiter.Segments[^1].Cacheable);
        Assert.Equal(3, _arbiter.Segments.Count);
    }

    [Fact]
    public async Task WhenTheArbiterCutsTheItem_TheNarrationEnds_NothingIsAppended_AndTheModelIsStopped()
    {
        // The arbiter ends the item after the first sentence (a Critical pre-empted it). The
        // model would go on for ever — the narrator must stop it and add no template.
        _arbiter.AbortAfterSegments = 1;
        var head = Encoding.UTF8.GetBytes(LlmWire.Sse("Good morning Captain. We depart runway one six right. Initial tr")
            .Replace("data: [DONE]\n\n", "", StringComparison.Ordinal));
        var handler = new StreamHandler(() => new ScriptedStream([head], ScriptedStream.Ending.Hang));
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var result = await Narrator(handler).RunAsync(Plan("Never spoken."));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(4));
        Assert.True(result.Preempted);
        Assert.Equal(SpeechOutcome.Superseded, result.Outcome);
        Assert.Equal(["Good morning Captain."], SegmentTexts);
        Assert.Equal(0, result.TemplateSections);
        Assert.Equal("Good morning Captain.", result.Text);
    }

    [Fact]
    public async Task CallerCancelMidStream_Throws_AndStopsEverything()
    {
        var head = Encoding.UTF8.GetBytes(LlmWire.Sse("Good morning Captain. We depart runway one six right. Initial tr")
            .Replace("data: [DONE]\n\n", "", StringComparison.Ordinal));
        var handler = new StreamHandler(() => new ScriptedStream([head], ScriptedStream.Ending.Hang));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Narrator(handler).RunAsync(Plan(), cts.Token));

        Assert.DoesNotContain(SegmentTexts, s => s.Contains("163", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheSessionEvent_CarriesTheFiguresTheFeatureIsJudgedBy()
    {
        var eventLog = SpeechTestSupport.TempEventLog();
        var monitor = SpeechTestSupport.BriefingMonitor(_options);
        var narrator = new StreamingNarrator(
            new OpenAiChatClient(monitor, new HttpClient(Sse("Good morning Captain. Initial track one six eight degrees."))),
            _arbiter, eventLog, monitor, NullLogger<StreamingNarrator>.Instance);

        await narrator.RunAsync(Plan());
        var path = eventLog.Path;
        await eventLog.DisposeAsync();

        var line = File.ReadAllLines(path).Single(l => l.Contains("\"llm.stream\"", StringComparison.Ordinal));
        Assert.Contains("\"kind\":\"briefing.departure\"", line, StringComparison.Ordinal);
        Assert.Contains("\"firstTokenMs\":", line, StringComparison.Ordinal);
        Assert.Contains("\"firstAudioMs\":", line, StringComparison.Ordinal);
        Assert.Contains("\"sentencesSpoken\":1", line, StringComparison.Ordinal);
        Assert.Contains("\"templateTookOver\":true", line, StringComparison.Ordinal);
        Assert.Contains("\"takeoverReason\":\"verify-failed\"", line, StringComparison.Ordinal);
        Assert.Contains("\"sectionsFromTemplate\":4", line, StringComparison.Ordinal);
        Assert.Contains("\"preempted\":false", line, StringComparison.Ordinal);
        Assert.Contains("\"model\":\"test-model\"", line, StringComparison.Ordinal);
    }
}
