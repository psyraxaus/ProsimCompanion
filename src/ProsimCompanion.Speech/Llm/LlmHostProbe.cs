using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Speech.Llm;

/// <summary>What the host probe last saw, as one immutable view.</summary>
/// <param name="Host">The host part of <c>briefing.llmBaseUrl</c>, or null when none is configured.</param>
/// <param name="Port">The port the base URL names (explicit or the scheme default).</param>
/// <param name="HostReachable">True when the last TCP connect to host:port succeeded; null
/// before the first check or with no host configured.</param>
/// <param name="LastCheckedUtc">When the last TCP check finished.</param>
/// <param name="ConsecutiveFailures">TCP checks failed in a row since the last success.</param>
/// <param name="MetricsAvailable">llama-server answered <c>GET /metrics</c> with its
/// <c>llamacpp:</c> series (started with <c>--metrics</c>); null = not yet checked.</param>
/// <param name="KvCacheUsagePercent">llama-server's KV cache fill when metrics are available.</param>
/// <param name="VramGb">Resident model size reported by Ollama's <c>/api/ps</c> (sum of
/// <c>size_vram</c>) when the API flavour is Ollama; null otherwise.</param>
/// <param name="LoadedModel">The model Ollama reports loaded, when known.</param>
public sealed record LlmHostView(
    string? Host,
    int Port,
    bool? HostReachable,
    DateTimeOffset? LastCheckedUtc,
    int ConsecutiveFailures,
    bool? MetricsAvailable,
    double? KvCacheUsagePercent,
    double? VramGb,
    string? LoadedModel)
{
    public static LlmHostView Empty { get; } = new(null, 0, null, null, 0, null, null, null, null);
}

/// <summary>
/// Sits beside <see cref="LlmHealthProbeService"/> and answers the question that service
/// cannot: when the LLM is unreachable, is the MACHINE down (asleep, Wake-on-LAN territory,
/// amber) or is the machine up with the server down (red)? A TCP connect to host:port every
/// <see cref="Interval"/> tells them apart without an ICMP privilege or an SSH dependency.
/// <para>
/// GPU utilisation is NOT available from any endpoint the app talks to: llama-server's
/// <c>/metrics</c> (Prometheus, needs <c>--metrics</c>) carries throughput and KV-cache
/// figures but no GPU or VRAM numbers, and Ollama exposes no utilisation at all. What can be
/// read is read: the KV-cache fill from llama-server, and the resident model size from
/// Ollama's <c>/api/ps</c> (its <c>size_vram</c> is the VRAM the model occupies). The VOICE
/// page's GPU gauge therefore shows XX by design; the VRAM readout fills on Ollama.
/// </para>
/// </summary>
/// <summary>The read side of <see cref="LlmHostProbe"/>, so the VOICE collector (and its tests)
/// need only the view.</summary>
public interface ILlmHostView
{
    /// <summary>The last view, never null.</summary>
    LlmHostView View { get; }
}

public sealed class LlmHostProbe : BackgroundService, ILlmHostView
{
    /// <summary>How often the TCP check runs; the diagnostics page ticks faster, the host
    /// state does not.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MetricsRecheck = TimeSpan.FromMinutes(10);

    private readonly IOptionsMonitor<BriefingOptions> _options;
    private readonly ILogger<LlmHostProbe> _logger;
    private readonly HttpClient _http;
    private volatile LlmHostView _view = LlmHostView.Empty;
    private DateTimeOffset _metricsCheckedUtc = DateTimeOffset.MinValue;
    private string? _metricsCheckedFor;

    public LlmHostProbe(IOptionsMonitor<BriefingOptions> options, ILogger<LlmHostProbe> logger, HttpClient? http = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _logger = logger;
        _http = http ?? new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };
    }

    /// <summary>The last view, never null.</summary>
    public LlmHostView View => _view;

    /// <summary>The host and port of the configured base URL, or null when not parseable.
    /// Exposed for tests and for the VOICE page's endpoint readout.</summary>
    public static (string Host, int Port)? Endpoint(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host))
        {
            return null;
        }

        return (uri.Host, uri.IsDefaultPort ? (uri.Scheme == "https" ? 443 : 80) : uri.Port);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                await ProbeOnceAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LLM host probe loop stopped unexpectedly; the VOICE page shows XX for the host");
        }
    }

    /// <summary>One cycle, exposed for tests: TCP check, then (rarely) the metrics look-up.</summary>
    public async Task ProbeOnceAsync(CancellationToken ct)
    {
        var options = _options.CurrentValue;
        if (!options.LlmEnabled || Endpoint(options.LlmBaseUrl) is not { } endpoint)
        {
            _view = LlmHostView.Empty;
            return;
        }

        var previous = _view;
        var reachable = await TcpReachableAsync(endpoint.Host, endpoint.Port, ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var failures = reachable ? 0 : previous.ConsecutiveFailures + 1;
        if (previous.HostReachable != reachable)
        {
            _logger.LogInformation(
                "LLM host {Host}:{Port} is {State}", endpoint.Host, endpoint.Port, reachable ? "reachable" : "not answering on TCP");
        }

        var view = previous with
        {
            Host = endpoint.Host,
            Port = endpoint.Port,
            HostReachable = reachable,
            LastCheckedUtc = now,
            ConsecutiveFailures = failures,
        };

        if (reachable)
        {
            view = await EnrichAsync(view, options, now, ct).ConfigureAwait(false);
        }

        _view = view;
    }

    private static async Task<bool> TcpReachableAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ConnectTimeout);
            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Ollama: the loaded model and its VRAM every cycle (cheap). llama-server:
    /// <c>/metrics</c> once per base URL, re-checked every ten minutes while absent.</summary>
    private async Task<LlmHostView> EnrichAsync(LlmHostView view, BriefingOptions options, DateTimeOffset now, CancellationToken ct)
    {
        var root = options.LlmBaseUrl.Trim().TrimEnd('/');
        if (options.LlmApi == LlmApiKind.Ollama)
        {
            try
            {
                using var doc = JsonDocument.Parse(await _http.GetStringAsync(root + "/api/ps", ct).ConfigureAwait(false));
                double vram = 0;
                string? model = null;
                if (doc.RootElement.TryGetProperty("models", out var models))
                {
                    foreach (var m in models.EnumerateArray())
                    {
                        if (m.TryGetProperty("size_vram", out var size) && size.TryGetDouble(out var bytes))
                        {
                            vram += bytes;
                        }

                        model ??= m.TryGetProperty("name", out var name) ? name.GetString() : null;
                    }
                }

                return view with { VramGb = vram > 0 ? Math.Round(vram / (1024 * 1024 * 1024), 1) : null, LoadedModel = model, MetricsAvailable = false };
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
            {
                _logger.LogDebug(ex, "Ollama /api/ps not readable");
                return view with { VramGb = null, LoadedModel = null, MetricsAvailable = false };
            }
        }

        if (view.MetricsAvailable == true || now - _metricsCheckedUtc < MetricsRecheck && _metricsCheckedFor == root)
        {
            return view.MetricsAvailable == true ? await ReadLlamaMetricsAsync(view, root, ct).ConfigureAwait(false) : view;
        }

        _metricsCheckedUtc = now;
        _metricsCheckedFor = root;
        try
        {
            var text = await _http.GetStringAsync(root + "/metrics", ct).ConfigureAwait(false);
            var available = text.Contains("llamacpp:", StringComparison.Ordinal);
            _logger.LogInformation(
                available
                    ? "llama-server metrics found at {Url}; KV-cache fill will show on the VOICE page"
                    : "No llama-server metrics at {Url} (start it with --metrics to expose them)",
                root + "/metrics");
            var enriched = view with { MetricsAvailable = available };
            return available ? Apply(enriched, text) : enriched;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogDebug(ex, "No /metrics at {Root}", root);
            return view with { MetricsAvailable = false };
        }
    }

    private async Task<LlmHostView> ReadLlamaMetricsAsync(LlmHostView view, string root, CancellationToken ct)
    {
        try
        {
            return Apply(view, await _http.GetStringAsync(root + "/metrics", ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogDebug(ex, "llama-server metrics read failed");
            return view with { KvCacheUsagePercent = null };
        }
    }

    /// <summary>Pulls the KV-cache ratio out of a Prometheus text body. Exposed for tests.</summary>
    public static LlmHostView Apply(LlmHostView view, string prometheusText)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(prometheusText);
        double? kv = null;
        foreach (var raw in prometheusText.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("llamacpp:kv_cache_usage_ratio", StringComparison.Ordinal))
            {
                continue;
            }

            var space = line.LastIndexOf(' ');
            if (space > 0 && double.TryParse(line[(space + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var ratio))
            {
                kv = Math.Round(ratio * 100, 0);
            }
        }

        return view with { KvCacheUsagePercent = kv };
    }

    public override void Dispose()
    {
        _http.Dispose();
        base.Dispose();
    }
}
