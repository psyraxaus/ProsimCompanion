using System.Net;
using System.Text;
using System.Text.Json;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Llm;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>
/// <see cref="OpenAiChatClient.StreamAsync"/> against a stub handler for both wire formats
/// (issue #147): SSE for the OpenAI shape, NDJSON for Ollama — whole, and cut into byte
/// chunks that split JSON objects and multi-byte characters.
/// </summary>
public sealed class OpenAiChatClientStreamTests
{
    // Made at run time: a fixed text here reads as a hardcoded credential to the code scanner.
    private static readonly string ApiKey = Guid.NewGuid().ToString("N");

    private static BriefingOptions OpenAi() => new()
    {
        LlmEnabled = true,
        LlmBaseUrl = "http://llm.test/v1",
        LlmModel = "test-model",
        LlmApiKey = ApiKey,
        LlmMaxTokens = 300,
    };

    private static BriefingOptions Ollama() => new()
    {
        LlmEnabled = true,
        LlmApi = LlmApiKind.Ollama,
        LlmBaseUrl = "http://ollama.test:11434",
        LlmModel = "qwen3.6:27b",
    };

    private static OpenAiChatClient Client(
        BriefingOptions options, HttpMessageHandler handler, LlmHealthStore? health = null, double floorSeconds = 5)
        => new(SpeechTestSupport.BriefingMonitor(options), new HttpClient(handler), health)
        {
            StreamTimeoutFloorSeconds = floorSeconds,
        };

    private static async Task<List<string>> Collect(IAsyncEnumerable<string> stream)
    {
        var deltas = new List<string>();
        await foreach (var delta in stream)
        {
            deltas.Add(delta);
        }

        return deltas;
    }

    // A briefing with characters that take 2 and 3 bytes in UTF-8, so a byte slice lands
    // inside one sooner or later.
    private static readonly string[] Deltas =
        ["Departing Zür", "ich, runway one six. ", "Wind 250° at 14 knots — QNH ", "1013."];

    // ---- OpenAI shape (SSE) ----

    [Fact]
    public async Task OpenAi_Sse_YieldsTheDeltas_SkipsRoleChunkAndKeepAlive_StopsAtDone()
    {
        var handler = new StreamHandler(() => LlmWire.Whole(LlmWire.Sse(Deltas) + "data: {\"choices\":[{\"delta\":{\"content\":\"AFTER DONE\"}}]}\n\n"));

        var deltas = await Collect(Client(OpenAi(), handler).StreamAsync("sys", "usr"));

        Assert.Equal(Deltas, deltas);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(64)]
    public async Task OpenAi_ChunksSplitMidJsonAndMidUtf8_GiveTheSameText(int chunkBytes)
    {
        var handler = new StreamHandler(() => LlmWire.Sliced(LlmWire.Sse(Deltas), chunkBytes));

        var deltas = await Collect(Client(OpenAi(), handler).StreamAsync("sys", "usr"));

        Assert.Equal(string.Concat(Deltas), string.Concat(deltas));
        Assert.Equal(Deltas.Length, deltas.Count);
    }

    [Fact]
    public async Task OpenAi_Request_AsksForAStream_AndKeepsTheModelAndLimit()
    {
        var handler = new StreamHandler(() => LlmWire.Whole(LlmWire.Sse("Hi.")));

        await Collect(Client(OpenAi(), handler).StreamAsync("sys", "usr"));

        using var doc = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        Assert.True(doc.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("test-model", doc.RootElement.GetProperty("model").GetString());
        Assert.Equal(300, doc.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public void BuildRequest_WithoutTheFlag_IsStillNonStreaming()
    {
        // CompleteAsync and its callers are untouched by this package.
        var (_, openAi) = OpenAiChatClient.BuildRequest(OpenAi(), "s", "u");
        var (_, ollama) = OpenAiChatClient.BuildRequest(Ollama(), "s", "u");

        using var a = JsonDocument.Parse(openAi);
        using var b = JsonDocument.Parse(ollama);
        Assert.False(a.RootElement.GetProperty("stream").GetBoolean());
        Assert.False(b.RootElement.GetProperty("stream").GetBoolean());
    }

    // ---- Ollama shape (NDJSON) ----

    [Fact]
    public async Task Ollama_Ndjson_YieldsContent_NeverThinking_StopsAtDoneTrue()
    {
        var body =
            "{\"message\":{\"role\":\"assistant\",\"content\":\"\",\"thinking\":\"The captain wants 9999 feet\"},\"done\":false}\n"
            + LlmWire.Ndjson(Deltas)
            + "{\"message\":{\"content\":\"AFTER DONE\"},\"done\":false}\n";
        var handler = new StreamHandler(() => LlmWire.Whole(body));

        var deltas = await Collect(Client(Ollama(), handler).StreamAsync("sys", "usr"));

        Assert.Equal(Deltas, deltas);
        Assert.DoesNotContain(deltas, d => d.Contains("9999", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(33)]
    public async Task Ollama_ChunksSplitMidJsonAndMidUtf8_GiveTheSameText(int chunkBytes)
    {
        var handler = new StreamHandler(() => LlmWire.Sliced(LlmWire.Ndjson(Deltas), chunkBytes));

        var deltas = await Collect(Client(Ollama(), handler).StreamAsync("sys", "usr"));

        Assert.Equal(string.Concat(Deltas), string.Concat(deltas));
    }

    [Fact]
    public async Task Ollama_Request_AsksForAStream_AndStillSendsThinkExplicitly()
    {
        var handler = new StreamHandler(() => LlmWire.Whole(LlmWire.Ndjson("Hi.")));

        await Collect(Client(Ollama(), handler).StreamAsync("sys", "usr"));

        using var doc = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        Assert.True(doc.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal(JsonValueKind.False, doc.RootElement.GetProperty("think").ValueKind);
    }

    // ---- line parser ----

    [Fact]
    public void ParseStreamLine_OpenAi()
    {
        Assert.Equal("Hi", OpenAiChatClient.ParseStreamLine(LlmApiKind.OpenAi, "data: {\"choices\":[{\"delta\":{\"content\":\"Hi\"}}]}", out var done));
        Assert.False(done);
        Assert.Null(OpenAiChatClient.ParseStreamLine(LlmApiKind.OpenAi, "data: [DONE]", out done));
        Assert.True(done);
        Assert.Null(OpenAiChatClient.ParseStreamLine(LlmApiKind.OpenAi, "", out _));
        Assert.Null(OpenAiChatClient.ParseStreamLine(LlmApiKind.OpenAi, ": ping", out _));
        Assert.Null(OpenAiChatClient.ParseStreamLine(LlmApiKind.OpenAi, "event: message", out _));
        // A usage-only final chunk (empty choices) and a finish chunk (no content) carry no text.
        Assert.Null(OpenAiChatClient.ParseStreamLine(LlmApiKind.OpenAi, "data: {\"choices\":[],\"usage\":{\"total_tokens\":9}}", out _));
        Assert.Null(OpenAiChatClient.ParseStreamLine(LlmApiKind.OpenAi, "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}", out _));
        // No space after the colon is valid SSE.
        Assert.Equal("x", OpenAiChatClient.ParseStreamLine(LlmApiKind.OpenAi, "data:{\"choices\":[{\"delta\":{\"content\":\"x\"}}]}", out _));
    }

    [Fact]
    public void ParseStreamLine_Ollama()
    {
        Assert.Equal("Hi", OpenAiChatClient.ParseStreamLine(LlmApiKind.Ollama, "{\"message\":{\"content\":\"Hi\"},\"done\":false}", out var done));
        Assert.False(done);
        Assert.Equal("", OpenAiChatClient.ParseStreamLine(LlmApiKind.Ollama, "{\"message\":{\"content\":\"\"},\"done\":true}", out done));
        Assert.True(done);
        Assert.Null(OpenAiChatClient.ParseStreamLine(LlmApiKind.Ollama, "{\"message\":{\"thinking\":\"hmm\"},\"done\":false}", out _));
        Assert.Null(OpenAiChatClient.ParseStreamLine(LlmApiKind.Ollama, "   ", out _));
    }

    [Fact]
    public void ParseStreamLine_MalformedJson_Throws()
    {
        Assert.ThrowsAny<JsonException>(() => OpenAiChatClient.ParseStreamLine(LlmApiKind.Ollama, "{\"message\":", out _));
        Assert.ThrowsAny<JsonException>(() => OpenAiChatClient.ParseStreamLine(LlmApiKind.OpenAi, "data: {oops", out _));
    }

    // ---- health, timeouts, cancellation ----

    [Fact]
    public async Task FirstDelta_ReportsHealthy()
    {
        var health = new LlmHealthStore();
        var handler = new StreamHandler(() => LlmWire.Whole(LlmWire.Sse("Hi.")));

        await Collect(Client(OpenAi(), handler, health).StreamAsync("s", "u"));

        Assert.Equal(LlmHealthState.Healthy, health.Snapshot().State);
    }

    [Fact]
    public async Task Http401_MarksAuthFailed_AndTheSummaryNeverCarriesTheKey()
    {
        var health = new LlmHealthStore();
        var handler = new StreamHandler(() => LlmWire.Whole(""), HttpStatusCode.Unauthorized);

        await Assert.ThrowsAsync<HttpRequestException>(() => Collect(Client(OpenAi(), handler, health).StreamAsync("s", "u")));

        var snapshot = health.Snapshot();
        Assert.Equal(LlmHealthState.AuthFailed, snapshot.State);
        Assert.DoesNotContain(ApiKey, snapshot.LastError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NoTextWithinTheBudget_ThrowsTimeout_AndMarksUnreachable()
    {
        // A thinking model that streams reasoning but no text does not count as answering.
        var health = new LlmHealthStore();
        var options = Ollama();
        options.LlmTimeoutSeconds = 0;
        var thinking = Encoding.UTF8.GetBytes("{\"message\":{\"thinking\":\"hmm\"},\"done\":false}\n");
        var handler = new StreamHandler(() => new ScriptedStream([thinking], ScriptedStream.Ending.Hang));

        var ex = await Assert.ThrowsAsync<TimeoutException>(
            () => Collect(Client(options, handler, health, floorSeconds: 0.2).StreamAsync("s", "u")));

        Assert.Contains("timed out", ex.Message, StringComparison.Ordinal);
        Assert.Equal(LlmHealthState.Unreachable, health.Snapshot().State);
    }

    [Fact]
    public async Task SilenceAfterTheFirstText_AlsoTimesOut_AndTheTextSoFarWasDelivered()
    {
        var options = OpenAi();
        options.LlmTimeoutSeconds = 0;
        var head = Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"Departing Sydney. \"}}]}\n\n");
        var handler = new StreamHandler(() => new ScriptedStream([head], ScriptedStream.Ending.Hang));
        var deltas = new List<string>();

        await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            await foreach (var delta in Client(options, handler, floorSeconds: 0.2).StreamAsync("s", "u"))
            {
                deltas.Add(delta);
            }
        });

        Assert.Equal("Departing Sydney. ", Assert.Single(deltas));
    }

    [Fact]
    public async Task ABrokenConnection_Throws_AndMarksUnreachable()
    {
        var health = new LlmHealthStore();
        var head = Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"Departing \"}}]}\n\n");
        var handler = new StreamHandler(() => new ScriptedStream([head], ScriptedStream.Ending.Break));

        await Assert.ThrowsAnyAsync<IOException>(() => Collect(Client(OpenAi(), handler, health).StreamAsync("s", "u")));

        Assert.Equal(LlmHealthState.Unreachable, health.Snapshot().State);
    }

    [Fact]
    public async Task CallerCancelMidStream_ThrowsCancelled_AndNeverPoisonsTheHealthState()
    {
        var health = new LlmHealthStore();
        var head = Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"Departing \"}}]}\n\n");
        var handler = new StreamHandler(() => new ScriptedStream([head], ScriptedStream.Ending.Hang));
        using var cts = new CancellationTokenSource();
        var deltas = new List<string>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var delta in Client(OpenAi(), handler, health).StreamAsync("s", "u", cts.Token))
            {
                deltas.Add(delta);
                cts.Cancel();
            }
        });

        Assert.Single(deltas);
        // Healthy from the first delta; the cancel must not turn it into Unreachable.
        Assert.Equal(LlmHealthState.Healthy, health.Snapshot().State);
    }

    [Fact]
    public async Task AnEndpointThatAnswersWithNoText_YieldsNothing_AndIsStillHealthy()
    {
        var health = new LlmHealthStore();
        var handler = new StreamHandler(() => LlmWire.Whole("data: [DONE]\n\n"));

        Assert.Empty(await Collect(Client(OpenAi(), handler, health).StreamAsync("s", "u")));
        Assert.Equal(LlmHealthState.Healthy, health.Snapshot().State);
    }
}
