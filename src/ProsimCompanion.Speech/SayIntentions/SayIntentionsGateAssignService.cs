using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Gate;

namespace ProsimCompanion.Speech.SayIntentions;

/// <summary>
/// SayIntentions arrival-gate push (<c>GET /sapi/assignGate?api_key&amp;gate&amp;airport</c>)
/// — the ATC half of the arrival-gate workflow, behind the Core
/// <see cref="ISayIntentionsGateAssign"/> seam. Like the weather service, only the API key is
/// required (not an active flight), and the key is re-read per call because flight.json keys
/// change between SayIntentions sessions. The key and the full request URL are never logged.
/// </summary>
public sealed class SayIntentionsGateAssignService : ISayIntentionsGateAssign
{
    private const string ApiBase = "https://apipri.sayintentions.ai/sapi";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly IOptionsMonitor<SayIntentionsOptions> _options;
    private readonly ILogger<SayIntentionsGateAssignService> _logger;

    public SayIntentionsGateAssignService(
        IOptionsMonitor<SayIntentionsOptions> options,
        ILogger<SayIntentionsGateAssignService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsActive
    {
        get
        {
            var options = _options.CurrentValue;
            return options.Enabled && ResolveApiKey(options) is not null;
        }
    }

    /// <inheritdoc />
    public async Task<SayIntentionsGateAssignResult> AssignGateAsync(
        string airportIcao, string gate, CancellationToken cancellationToken = default)
    {
        var options = _options.CurrentValue;
        if (!options.Enabled)
        {
            return new(false, true, "SayIntentions disabled — ATC assignment skipped.");
        }

        var apiKey = ResolveApiKey(options);
        if (apiKey is null)
        {
            return new(false, true, "No SayIntentions API key (flight.json absent?) — ATC assignment skipped.");
        }

        // assignGate accepts alphanumerics only, max 30 chars (predecessor rule).
        var normalizedGate = new string((gate ?? "").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (normalizedGate.Length == 0)
        {
            return new(false, false, "Gate has no alphanumeric characters.");
        }

        if (normalizedGate.Length > 30)
        {
            normalizedGate = normalizedGate[..30];
        }

        var icao = (airportIcao ?? "").Trim().ToUpperInvariant();
        if (icao.Length == 0)
        {
            return new(false, false, "Missing airport ICAO.");
        }

        try
        {
            var url = $"{ApiBase}/assignGate?api_key={Uri.EscapeDataString(apiKey)}"
                + $"&gate={Uri.EscapeDataString(normalizedGate)}&airport={Uri.EscapeDataString(icao)}";
            using var response = await Http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                _logger.LogWarning("SayIntentions assignGate HTTP {Status} for {Airport}/{Gate}",
                    status, icao, normalizedGate);
                return new(false, false, status >= 500
                    ? $"SayIntentions unavailable (HTTP {status}) — confirm a SayIntentions flight is active and retry."
                    : $"HTTP {status}");
            }

            var json = JsonNode.Parse(body);
            var error = json?["error"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(error))
            {
                _logger.LogWarning("SayIntentions assignGate returned error: {Error}", error);
                return new(false, false, error);
            }

            var assigned = json?["assigned_gate_name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(assigned))
            {
                _logger.LogWarning("SayIntentions assignGate response missing assigned_gate_name");
                return new(false, false, "Response missing assigned_gate_name.");
            }

            _logger.LogInformation("SayIntentions gate assigned at {Airport}: {Gate}", icao, assigned);
            return new(true, false, assigned);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SayIntentions assignGate failed");
            return new(false, false, ex.Message);
        }
    }

    /// <summary>Same key resolution as the weather service: manual key when configured,
    /// otherwise flight.json → flight_details.api_key, re-read per call.</summary>
    private string? ResolveApiKey(SayIntentionsOptions options)
    {
        if (options.ApiKeySource.Equals("manual", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(options.ManualApiKey) ? null : options.ManualApiKey;
        }

        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SayIntentionsAI", "flight.json");
            if (!File.Exists(path))
            {
                return null;
            }

            var root = JsonNode.Parse(FlightJsonFile.ReadAllText(path));
            var key = (root?["flight_details"] ?? root)?["api_key"]?.GetValue<string>();
            return string.IsNullOrWhiteSpace(key) ? null : key;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "flight.json api-key read failed");
            return null;
        }
    }
}
