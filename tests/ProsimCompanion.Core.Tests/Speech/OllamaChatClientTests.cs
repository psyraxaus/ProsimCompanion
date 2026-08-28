using System.Net;
using System.Text;
using System.Text.Json;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Speech.Llm;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>
/// The Ollama native <c>/api/chat</c> flavour of <see cref="OpenAiChatClient"/>: the wire body
/// must carry <c>think</c> EXPLICITLY in both states (thinking models default it on), the
/// OpenAI flavour must be untouched, and a reply's <c>message.thinking</c> must never leak
/// into the text that goes to TTS.
/// </summary>
public sealed class OllamaChatClientTests
{
    private static BriefingOptions Ollama(bool thinking) => new()
    {
        LlmEnabled = true,
        LlmApi = LlmApiKind.Ollama,
        LlmBaseUrl = "http://ollama.test:11434/",
        LlmModel = "qwen3.6:27b",
        LlmMaxTokens = 256,
        LlmEnableThinking = thinking,
    };

    // ---- Request serialization ----

    [Fact]
    public void Ollama_ThinkingOff_SendsThinkFalseExplicitly()
    {
        var (url, body) = OpenAiChatClient.BuildRequest(Ollama(thinking: false), "sys", "usr");

        Assert.Equal("http://ollama.test:11434/api/chat", url);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("think", out var think));
        Assert.Equal(JsonValueKind.False, think.ValueKind);
        Assert.Equal("qwen3.6:27b", root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public void Ollama_ThinkingOn_SendsThinkTrue()
    {
        var (_, body) = OpenAiChatClient.BuildRequest(Ollama(thinking: true), "sys", "usr");

        using var doc = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.True, doc.RootElement.GetProperty("think").ValueKind);
    }

    [Fact]
    public void Ollama_SamplingLimitsLiveInOptions_NotTopLevel()
    {
        var (_, body) = OpenAiChatClient.BuildRequest(Ollama(thinking: false), "sys", "usr");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.Equal(256, root.GetProperty("options").GetProperty("num_predict").GetInt32());
        Assert.False(root.TryGetProperty("max_tokens", out _));
    }

    [Fact]
    public void Ollama_MessagesKeepTheOpenAiShape()
    {
        var (_, body) = OpenAiChatClient.BuildRequest(Ollama(thinking: false), "be brief", "ping");

        using var doc = JsonDocument.Parse(body);
        var messages = doc.RootElement.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("be brief", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("ping", messages[1].GetProperty("content").GetString());
    }

    [Fact]
    public void OpenAi_FlavourIsUnchanged_NoThinkField()
    {
        var options = new BriefingOptions
        {
            LlmApi = LlmApiKind.OpenAi,
            LlmBaseUrl = "http://llm.test/v1",
            LlmModel = "gpt-4o-mini",
            LlmMaxTokens = 128,
            LlmEnableThinking = true, // ignored on this flavour — nothing to send it as
        };

        var (url, body) = OpenAiChatClient.BuildRequest(options, "sys", "usr");

        Assert.Equal("http://llm.test/v1/chat/completions", url);
        using var doc = JsonDocument.Parse(body);
        Assert.False(doc.RootElement.TryGetProperty("think", out _));
        Assert.False(doc.RootElement.TryGetProperty("options", out _));
        Assert.Equal(128, doc.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public void Default_IsOpenAiWithThinkingOff()
    {
        var options = new BriefingOptions();

        Assert.Equal(LlmApiKind.OpenAi, options.LlmApi);
        Assert.False(options.LlmEnableThinking);
    }

    // ---- Response parsing ----

    [Fact]
    public void NonStreaming_ReturnsContentOnly_ThinkingNeverLeaks()
    {
        const string body = """
            {"model":"qwen3.6:27b","message":{"role":"assistant","thinking":"Let me reason about this...","content":"OK"},"done":true}
            """;

        Assert.Equal("OK", OllamaChatResponse.ExtractContent(body));
    }

    [Fact]
    public void Ndjson_ConcatenatesContentChunks_IgnoresThinkingAndDoneLine()
    {
        const string body = """
            {"message":{"role":"assistant","thinking":"hmm","content":""},"done":false}
            {"message":{"role":"assistant","thinking":" more hmm","content":""},"done":false}
            {"message":{"role":"assistant","content":"Good "},"done":false}
            {"message":{"role":"assistant","content":"morning."},"done":false}

            {"message":{"role":"assistant","content":""},"done":true,"done_reason":"stop","total_duration":1}
            """;

        Assert.Equal("Good morning.", OllamaChatResponse.ExtractContent(body));
    }

    [Fact]
    public void Ndjson_ToleratesCrLfLineEndings()
    {
        var body = "{\"message\":{\"content\":\"a\"},\"done\":false}\r\n{\"message\":{\"content\":\"b\"},\"done\":true}\r\n";

        Assert.Equal("ab", OllamaChatResponse.ExtractContent(body));
    }

    [Fact]
    public void NoContentAnywhere_ReturnsNull()
    {
        Assert.Null(OllamaChatResponse.ExtractContent("""{"done":true}"""));
        Assert.Null(OllamaChatResponse.ExtractContent(""));
    }

    [Fact]
    public void MalformedBody_Throws()
    {
        Assert.ThrowsAny<JsonException>(() => OllamaChatResponse.ExtractContent("not json"));
    }

    // ---- End to end through the client ----

    private sealed class CapturingHandler(string reply) : HttpMessageHandler
    {
        public string? Url { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Url = request.RequestUri!.ToString();
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(reply, Encoding.UTF8, "application/json"),
            };
        }
    }

    [Fact]
    public async Task CompleteAsync_OllamaFlavour_HitsApiChatWithThinkFalse_AndReturnsContentOnly()
    {
        var handler = new CapturingHandler(
            """{"message":{"role":"assistant","thinking":"secret reasoning","content":"Cleared for takeoff."},"done":true}""");
        var client = new OpenAiChatClient(
            SpeechTestSupport.BriefingMonitor(Ollama(thinking: false)), new HttpClient(handler));

        var reply = await client.CompleteAsync("sys", "usr");

        Assert.Equal("Cleared for takeoff.", reply);
        Assert.Equal("http://ollama.test:11434/api/chat", handler.Url);
        Assert.Contains("\"think\":false", handler.Body);
        Assert.DoesNotContain("secret reasoning", reply);
    }
}
