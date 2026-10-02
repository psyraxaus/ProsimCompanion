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
        => BuildRequest(options, system, user, stream: false);

    /// <summary>As above, with the <c>stream</c> flag chosen: true asks the endpoint for token
    /// deltas (SSE for the OpenAI shape, NDJSON for Ollama — see <see cref="StreamAsync"/>).</summary>
    public static (string Url, string Body) BuildRequest(BriefingOptions options, string system, string user, bool stream)
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
                stream,
                think = options.LlmEnableThinking,
                options = new { num_predict = options.LlmMaxTokens },
            }, BodyJson)),

            _ => (root + "/chat/completions", JsonSerializer.Serialize(new
            {
                model = options.LlmModel,
                messages,
                max_tokens = options.LlmMaxTokens,
                stream,
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

    // ---- streaming (issue #147) ----

    /// <summary>Floor for the streaming timeout budget, seconds — the same 5 s floor
    /// <see cref="CompleteAsync"/> applies. Internal so tests can shorten it.</summary>
    internal double StreamTimeoutFloorSeconds { get; init; } = 5;

    /// <summary>The text delta one line of a streamed reply carries, or null when the line
    /// carries none (blank, an SSE comment or event line, a role-only or thinking-only chunk).
    /// <paramref name="done"/> is true on the terminator: <c>data: [DONE]</c> for the OpenAI
    /// shape, <c>"done": true</c> for Ollama. Throws <see cref="JsonException"/> on a line
    /// that should be JSON and is not. Never returns a thinking/reasoning field.</summary>
    public static string? ParseStreamLine(LlmApiKind api, string line, out bool done)
    {
        ArgumentNullException.ThrowIfNull(line);
        done = false;

        var text = line.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        if (api == LlmApiKind.Ollama)
        {
            using var chunk = JsonDocument.Parse(text);
            var root = chunk.RootElement;
            done = root.TryGetProperty("done", out var doneValue) && doneValue.ValueKind == JsonValueKind.True;
            return root.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.Object
                && message.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.String
                    ? content.GetString()
                    : null;
        }

        // Server-sent events: only "data:" lines carry payload; ":" lines are keep-alives.
        const string dataPrefix = "data:";
        if (!text.StartsWith(dataPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var payload = text[dataPrefix.Length..].Trim();
        if (payload == "[DONE]")
        {
            done = true;
            return null;
        }

        using var doc = JsonDocument.Parse(payload);
        return doc.RootElement.TryGetProperty("choices", out var choices)
            && choices.ValueKind == JsonValueKind.Array
            && choices.GetArrayLength() > 0
            && choices[0].TryGetProperty("delta", out var delta)
            && delta.ValueKind == JsonValueKind.Object
            && delta.TryGetProperty("content", out var deltaContent)
            && deltaContent.ValueKind == JsonValueKind.String
                ? deltaContent.GetString()
                : null;
    }

    /// <summary>
    /// One streamed chat completion: yields the assistant text as it arrives, delta by delta
    /// (SSE <c>data:</c> lines for the OpenAI shape, NDJSON for Ollama's <c>/api/chat</c>).
    /// The response is read line by line through a decoder, so a network chunk that splits a
    /// JSON object or a multi-byte character changes nothing.
    /// <para>
    /// One budget (<see cref="BriefingOptions.LlmTimeoutSeconds"/>, 5 s floor) guards two
    /// things: the FIRST text delta must arrive inside it (a model that only thinks does not
    /// count), and after that no gap between lines may exceed it. Running out throws
    /// <see cref="TimeoutException"/>; HTTP and transport failures throw as in
    /// <see cref="CompleteAsync"/>. A caller cancel throws <see cref="OperationCanceledException"/>
    /// and never touches the health state. Every other outcome is reported to
    /// <see cref="LlmHealthStore"/> exactly as the non-streaming path does.
    /// </para>
    /// </summary>
    public async IAsyncEnumerable<string> StreamAsync(
        string system,
        string user,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(user);

        var options = _options.CurrentValue;
        var budget = TimeSpan.FromSeconds(Math.Max(StreamTimeoutFloorSeconds, options.LlmTimeoutSeconds));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(budget);

        using var response = await OpenStreamAsync(options, system, user, budget, cts.Token, cancellationToken)
            .ConfigureAwait(false);
        using var body = await GuardAsync(
            () => response.Content.ReadAsStreamAsync(cts.Token), budget, cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(body, Encoding.UTF8);

        var sawText = false;
        while (true)
        {
            var line = await GuardAsync(
                () => reader.ReadLineAsync(cts.Token).AsTask(), budget, cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            var delta = ParseStreamLine(options.LlmApi, line, out var done);
            if (!string.IsNullOrEmpty(delta))
            {
                if (!sawText)
                {
                    sawText = true;
                    _health?.Report(LlmHealthState.Healthy);
                }

                yield return delta;
            }

            if (done)
            {
                break;
            }

            if (sawText)
            {
                // From the first text on, the budget is an idle guard: any line re-arms it.
                cts.CancelAfter(budget);
            }
        }

        if (!sawText)
        {
            // The endpoint answered and closed cleanly with no text: reachable, just silent.
            _health?.Report(LlmHealthState.Healthy);
        }
    }

    /// <summary>Sends the streaming request and returns once the response HEADERS are in
    /// (the body is read by the caller). Health reporting as in <see cref="CompleteAsync"/>.</summary>
    private async Task<HttpResponseMessage> OpenStreamAsync(
        BriefingOptions options, string system, string user, TimeSpan budget,
        CancellationToken linkedToken, CancellationToken callerToken)
    {
        var (url, requestBody) = BuildRequest(options, system, user, stream: true);
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (!string.IsNullOrWhiteSpace(options.LlmApiKey))
        {
            request.Headers.Authorization = new("Bearer", options.LlmApiKey);
        }

        request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");

        HttpResponseMessage? response = null;
        var statusReported = false;
        try
        {
            response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // The summary carries only the status code, NEVER the key.
                var status = (int)response.StatusCode;
                _health?.Report(
                    status is 401 or 403 ? LlmHealthState.AuthFailed : LlmHealthState.Unreachable,
                    $"HTTP {status} from the chat endpoint");
                statusReported = true;
            }

            response.EnsureSuccessStatusCode();
            return response;
        }
        catch (HttpRequestException ex)
        {
            response?.Dispose();
            if (!statusReported)
            {
                _health?.Report(LlmHealthState.Unreachable, ex.Message);
            }

            throw;
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
            response?.Dispose();
            throw TimedOut(budget);
        }
        catch
        {
            response?.Dispose();
            throw;
        }
    }

    /// <summary>Runs one read of the streamed body, turning our own budget expiring into a
    /// reported <see cref="TimeoutException"/> and a dropped connection into a reported
    /// transport failure. A caller cancel passes through untouched.</summary>
    private async Task<T> GuardAsync<T>(Func<Task<T>> read, TimeSpan budget, CancellationToken callerToken)
    {
        try
        {
            return await read().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
            throw TimedOut(budget);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            _health?.Report(LlmHealthState.Unreachable, "the chat stream broke: " + ex.Message);
            throw;
        }
    }

    private TimeoutException TimedOut(TimeSpan budget)
    {
        var summary = $"timed out after {budget.TotalSeconds:0.#} s (streaming)";
        _health?.Report(LlmHealthState.Unreachable, summary);
        return new TimeoutException("The chat endpoint " + summary + ".");
    }
}
