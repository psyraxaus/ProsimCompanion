using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft.Gateway;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Logging;

namespace ProsimCompanion.Prosim.Gateway;

/// <summary>
/// HTTP client for the ProSim EFB gateway on port 5000. Requests serialize PascalCase and
/// responses read case-insensitively (the gateway answers camelCase). Transient failures retry
/// up to 3 attempts; METAR 204 ("no data") is a valid answer and never retried.
/// </summary>
public sealed class ProsimGatewayClient : IProsimGateway, IDisposable
{
    private const int GatewayPort = 5000;
    private const int RetryAttempts = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IOptionsMonitor<ProsimOptions> _options;
    private readonly ILogger<ProsimGatewayClient> _logger;
    private readonly IWireTrace _wire;
    private readonly HttpClient _http;

    public ProsimGatewayClient(
        IOptionsMonitor<ProsimOptions> options,
        ILogger<ProsimGatewayClient> logger,
        IWireTrace wire)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(wire);

        _options = options;
        _logger = logger;
        _wire = wire;
        _http = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    private Uri GraphQlUri => new($"http://{_options.CurrentValue.Host}:{GatewayPort}/graphql");

    /// <inheritdoc />
    public async Task<bool> IsReachableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(ProbeTimeout);
            // Any answer at all — 200, 404, whatever — proves the listener is up; only a
            // transport failure (refused, timeout) reports unreachable.
            using var request = new HttpRequestMessage(
                HttpMethod.Get, new Uri($"http://{_options.CurrentValue.Host}:{GatewayPort}/"));
            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            _logger.LogDebug("Gateway reachability probe: not reachable ({Message})", ex.Message);
            return false;
        }
    }

    private Uri EfbUri(string relative) => new($"http://{_options.CurrentValue.Host}:{GatewayPort}/efb{relative}");

    /// <inheritdoc />
    public async Task<bool> WriteDataRefAsync(string name, object value, CancellationToken cancellationToken = default)
    {
        // Same write gate as the SDK path — the transport must never widen the write surface.
        DataRefs.ProsimWriteGate.EnsureAllowed(name);

        var body = GraphQlMessages.BuildWriteMutation(name, value);
        _wire.Trace("Gateway", ">>", body);
        var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, GraphQlUri)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            },
            $"write {name}",
            cancellationToken).ConfigureAwait(false);
        return response is { IsSuccessStatusCode: true };
    }

    /// <inheritdoc />
    public async Task<string?> QueryDataRefAsync(string name, CancellationToken cancellationToken = default)
    {
        var body = GraphQlMessages.BuildQuery(name, "queryResult");
        var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, GraphQlUri)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            },
            $"query {name}",
            cancellationToken).ConfigureAwait(false);
        if (response is not { IsSuccessStatusCode: true })
        {
            return null;
        }

        try
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _wire.Trace("Gateway", "<<", text);
            return JsonNode.Parse(text)?["data"]?["dataRef"]?["queryResult"]?["value"]?.ToString();
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Gateway returned unparseable response for dataref query {DataRef}", name);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<bool> CancelBoardingAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, EfbUri("/tasks/cancelBoarding"))
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            },
            "cancelBoarding",
            cancellationToken).ConfigureAwait(false);
        return response is { IsSuccessStatusCode: true };
    }

    /// <inheritdoc />
    public Task<CalcVSpeedResult?> CalculateVSpeedsAsync(CalcVSpeedRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return PostJsonAsync<CalcVSpeedRequest, CalcVSpeedResult>("/calculate/vspeeds", request, cancellationToken);
    }

    /// <inheritdoc />
    public Task<CalcLdrResponse?> CalculateLandingDistanceAsync(CalcLdrRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return PostJsonAsync<CalcLdrRequest, CalcLdrResponse>("/calculate/ldr", request, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RunwayResponse>?> GetRunwaysAsync(string icao, bool includeIntersections, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(icao);
        var uri = EfbUri($"/airport/{Uri.EscapeDataString(icao)}/runways?includeIntersections={(includeIntersections ? "true" : "false")}");
        return await GetJsonAsync<List<RunwayResponse>>(uri, $"runways {icao}", cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<MetarFetchResult> GetMetarAsync(string icao, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(icao);
        var uri = EfbUri($"/airport/{Uri.EscapeDataString(icao)}/metar");

        var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, uri),
            $"metar {icao}",
            cancellationToken).ConfigureAwait(false);
        if (response is null)
        {
            return new MetarFetchResult(null, "gateway unreachable");
        }

        // 204 is the gateway's "no METAR available" answer — a success, not a retryable fault.
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return new MetarFetchResult(null, null);
        }

        if (!response.IsSuccessStatusCode)
        {
            // The status code IS the diagnosis (issue #62: a deterministic 500 spent a whole
            // flight rendered as a bare "No METAR available").
            return new MetarFetchResult(null, $"gateway HTTP {(int)response.StatusCode}");
        }

        var metar = await DeserializeAsync<Metar>(response, $"metar {icao}", cancellationToken).ConfigureAwait(false);
        return metar is null
            ? new MetarFetchResult(null, "gateway response unparseable")
            : new MetarFetchResult(metar, null);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FailuresResponse>?> GetFailuresAsync(CancellationToken cancellationToken = default)
        => await GetJsonAsync<List<FailuresResponse>>(EfbUri("/failures"), "failures", cancellationToken).ConfigureAwait(false);

    public void Dispose() => _http.Dispose();

    private async Task<TResponse?> PostJsonAsync<TRequest, TResponse>(
        string relative,
        TRequest request,
        CancellationToken cancellationToken)
        where TResponse : class
    {
        var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, EfbUri(relative))
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(request, SerializerOptions),
                    Encoding.UTF8,
                    "application/json"),
            },
            relative,
            cancellationToken).ConfigureAwait(false);
        if (response is not { IsSuccessStatusCode: true })
        {
            return null;
        }

        return await DeserializeAsync<TResponse>(response, relative, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TResponse?> GetJsonAsync<TResponse>(Uri uri, string operation, CancellationToken cancellationToken)
        where TResponse : class
    {
        var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, uri),
            operation,
            cancellationToken).ConfigureAwait(false);
        if (response is not { IsSuccessStatusCode: true })
        {
            return null;
        }

        return await DeserializeAsync<TResponse>(response, operation, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TResponse?> DeserializeAsync<TResponse>(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
        where TResponse : class
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<TResponse>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Gateway response for {Operation} could not be parsed", operation);
            return null;
        }
    }

    /// <summary>Sends with retry; returns null only when no HTTP response was ever received
    /// (callers treat null as "gateway unreachable"). A 4xx returns immediately and a final
    /// failed 5xx attempt returns its response, so callers can report the real status code —
    /// all callers gate on <c>IsSuccessStatusCode</c>. Request messages are single-use, hence
    /// the factory.</summary>
    private async Task<HttpResponseMessage?> SendAsync(
        Func<HttpRequestMessage> requestFactory,
        string operation,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= RetryAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var request = requestFactory();
                var stopwatch = Stopwatch.StartNew();
                var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NoContent)
                {
                    _logger.LogDebug(
                        "Gateway {Operation} -> {StatusCode} in {Elapsed} ms",
                        operation,
                        (int)response.StatusCode,
                        stopwatch.ElapsedMilliseconds);
                    return response;
                }

                // Client errors (4xx) will not improve on retry; report them once.
                if ((int)response.StatusCode is >= 400 and < 500)
                {
                    _logger.LogWarning(
                        "Gateway rejected {Operation} with {StatusCode}",
                        operation,
                        (int)response.StatusCode);
                    return response;
                }

                _logger.LogWarning(
                    "Gateway {Operation} attempt {Attempt}/{Max} failed with {StatusCode}",
                    operation,
                    attempt,
                    RetryAttempts,
                    (int)response.StatusCode);

                // Final attempt: hand the failed response back so callers can surface the
                // actual status code (issue #62) — every caller already treats a non-success
                // response the same as null.
                if (attempt == RetryAttempts)
                {
                    return response;
                }

                response.Dispose();
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(
                    "Gateway {Operation} attempt {Attempt}/{Max} failed: {Message}",
                    operation,
                    attempt,
                    RetryAttempts,
                    ex.Message);
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "Gateway {Operation} attempt {Attempt}/{Max} timed out",
                    operation,
                    attempt,
                    RetryAttempts);
            }

            if (attempt < RetryAttempts)
            {
                await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }

        return null;
    }
}
