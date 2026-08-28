using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Speech.Llm;

/// <summary>
/// Minimal chat client shared by the LLM-styled compositions (briefing, debrief). Speaks
/// either the OpenAI chat-completions shape or Ollama's native <c>/api/chat</c>, chosen by
/// <see cref="BriefingOptions.LlmApi"/> — the native shape exists because it is the ONLY way
/// to switch a thinking model's reasoning phase off (<see cref="BriefingOptions.LlmEnableThinking"/>).
/// Deliberately configured from <see cref="BriefingOptions"/>' existing <c>Llm*</c> keys — one
/// LLM endpoint for the whole app, not a config section per feature.
/// Each call gets its OWN timeout budget (floor 5 s) from the current settings — two calls in
/// one composition (ask + strict re-ask) must not share a single expiring window, which was a
/// known predecessor bug. Failures throw; callers own their template fallback. Every outcome
/// is also reported to <see cref="LlmHealthStore"/> (when supplied) so a dead endpoint is
/// SURFACED instead of silently falling back all flight (issue #66: a 401 ran an entire
/// flight with one Debug-level trace as the only evidence).
/// </summary>
public sealed class OpenAiChatClient
{
    // No global timeout: the per-call CTS below carries the current configured budget, so a
    // settings change applies to the very next call.
    private static readonly HttpClient SharedHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

    private static readonly JsonSerializerOptions BodyJson = new(JsonSerializerDefaults.Web);

    private readonly IOptionsMonitor<BriefingOptions> _options;
    private readonly HttpClient _http;
    private readonly LlmHealthStore? _health;

    /// <summary>Production path — uses the process-wide shared HttpClient. The health store
    /// is optional so hand-constructed instances (tests, tools) keep working; the DI
    /// registration supplies it.</summary>
    public OpenAiChatClient(IOptionsMonitor<BriefingOptions> options, LlmHealthStore? health = null)
        : this(options, SharedHttp, health)
    {
    }

    /// <summary>Test seam: supply an HttpClient over a fake handler.</summary>
    public OpenAiChatClient(IOptionsMonitor<BriefingOptions> options, HttpClient http, LlmHealthStore? health = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(http);

        _options = options;
        _http = http;
        _health = health;
    }

    /// <summary>True when the LLM is enabled and a model is named — the gate every styled
    /// composition checks before spending a call.</summary>
    public bool IsConfigured
        => _options.CurrentValue.LlmEnabled
            && !string.IsNullOrWhiteSpace(_options.CurrentValue.LlmModel);

    /// <summary>Endpoint URL and serialized request body for one call, extracted pure so the
    /// wire shape is testable per API flavour without an HTTP stack.</summary>
    public static (string Url, string Body) BuildRequest(BriefingOptions options, string system, string user)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(user);

        var root = options.LlmBaseUrl.TrimEnd('/');
        var messages = new[]
        {
            new { role = "system", content = system },
            new { role = "user", content = user },
        };

        return options.LlmApi switch
        {
            // "think" goes out in BOTH states on purpose: thinking-capable models default it
            // ON, so omitting the field when false would silently re-enable the slow phase.
            LlmApiKind.Ollama => (root + "/api/chat", JsonSerializer.Serialize(new
            {
                model = options.LlmModel,
                messages,
                stream = false,
                think = options.LlmEnableThinking,
                options = new { num_predict = options.LlmMaxTokens },
            }, BodyJson)),

            _ => (root + "/chat/completions", JsonSerializer.Serialize(new
            {
                model = options.LlmModel,
                messages,
                max_tokens = options.LlmMaxTokens,
                stream = false,
            }, BodyJson)),
        };
    }

    /// <summary>Assistant text from a response body in the given flavour; null when the
    /// endpoint returned none. Never includes a thinking/reasoning field.</summary>
    public static string? ParseReply(LlmApiKind api, string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        if (api == LlmApiKind.Ollama)
        {
            return OllamaChatResponse.ExtractContent(body);
        }

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message")
            .GetProperty("content").GetString();
    }

    /// <summary>One non-streaming chat completion; returns the assistant message content
    /// (null when the endpoint returns none). Throws on HTTP/timeout/parse failure.</summary>
    public async Task<string?> CompleteAsync(string system, string user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(user);

        var options = _options.CurrentValue;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, options.LlmTimeoutSeconds)));

        var (url, body) = BuildRequest(options, system, user);
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (!string.IsNullOrWhiteSpace(options.LlmApiKey))
        {
            request.Headers.Authorization = new("Bearer", options.LlmApiKey);
        }

        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        var statusReported = false;
        try
        {
            using var response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // 401/403 is a credential problem (won't heal by itself); anything else is
                // lumped with transport failures — the host may recover. The summary carries
                // only the status code, NEVER the key.
                var status = (int)response.StatusCode;
                _health?.Report(
                    status is 401 or 403 ? LlmHealthState.AuthFailed : LlmHealthState.Unreachable,
                    $"HTTP {status} from the chat endpoint");
                statusReported = true;
            }

            response.EnsureSuccessStatusCode();
            var content = ParseReply(
                options.LlmApi,
                await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false));
            _health?.Report(LlmHealthState.Healthy);
            return content;
        }
        catch (HttpRequestException ex)
        {
            // Don't overwrite a just-reported HTTP status (EnsureSuccessStatusCode re-throws
            // through here) — that would demote AuthFailed to Unreachable.
            if (!statusReported)
            {
                _health?.Report(LlmHealthState.Unreachable, ex.Message);
            }

            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Our own timeout budget expired — an unreachable/overloaded host, not a caller
            // cancel (which must never poison the health state).
            _health?.Report(LlmHealthState.Unreachable,
                $"timed out after {Math.Max(5, options.LlmTimeoutSeconds)} s");
            throw;
        }
    }
}
