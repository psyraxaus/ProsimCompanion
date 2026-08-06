using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Checklists;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.SayIntentions;

/// <summary>One data-driven ATC request from config/atc-requests.json.</summary>
public sealed class AtcRequestDefinition
{
    public List<string> Phrases { get; set; } = [];
    public string Station { get; set; } = "";
    public string CommType { get; set; } = "";
    public bool IsDeparture { get; set; }
    public string Icao { get; set; } = "";
    public string Faa { get; set; } = "";
}

/// <summary>
/// SayIntentions integration (Prosim2FO semantics): flight.json polled at 1 Hz for the active
/// flight context, ATC requests transmitted via GET sayAs on COM1 (255-char cap), optional
/// getWX comms lookup for auto-tune via setFreq, and the departure-comms gate — a new active
/// flight hands comms to the SI copilot (SIAI_COPILOT=1); approaching the runway
/// (distance ≤ 0.3 nm) takes them back, tunes Tower, announces, and the takeoff request
/// restores them. The SIAI L:var radio-clear gate is replaced by the predecessor's own
/// no-SimVars fallback (a fixed 400 ms settle) until LVAR reads are wired here.
/// </summary>
public sealed class SayIntentionsService : IVoiceFeature, IDisposable
{
    private const string ApiBase = "https://apipri.sayintentions.ai/sapi";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly IOptionsMonitor<SayIntentionsOptions> _options;
    private readonly ISpeechArbiter _arbiter;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<SayIntentionsService> _logger;
    private readonly object _gate = new();

    private List<AtcRequestDefinition> _requests = [];
    private Timer? _timer;
    private FlightContext _flight = FlightContext.Empty;
    private bool _wasActive;
    private DepartureState _departure = DepartureState.Idle;

    private enum DepartureState
    {
        Idle,
        CommsTakenBack,
        Requested,
        Restored,
    }

    private sealed record FlightContext(
        bool IsActive,
        string? ApiKey,
        string? Callsign,
        string? Gate,
        string? DepartureRunway,
        string? Airport,
        string? Origin,
        double DistanceToRunway)
    {
        public static FlightContext Empty { get; } =
            new(false, null, null, null, null, null, null, double.MaxValue);
    }

    public SayIntentionsService(
        IOptionsMonitor<SayIntentionsOptions> options,
        ISpeechArbiter arbiter,
        JsonlEventLog eventLog,
        ILogger<SayIntentionsService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _arbiter = arbiter;
        _eventLog = eventLog;
        _logger = logger;
    }

    public IEnumerable<string> Phrases
        => _options.CurrentValue.Enabled ? _requests.SelectMany(r => r.Phrases) : [];

    public bool ValueParse => false;

    public void Start()
    {
        LoadRequests();
        _timer = new Timer(_ => PollFlightJson(), null, 1000, 1000);
    }

    public void Dispose() => _timer?.Dispose();

    public bool TryHandle(string utterance)
    {
        var options = _options.CurrentValue;
        if (!options.Enabled)
        {
            return false;
        }

        var text = CommandMatcher.Normalize(utterance);
        var request = _requests.FirstOrDefault(r =>
            r.Phrases.Any(p => CommandMatcher.Normalize(p).Equals(text, StringComparison.Ordinal)));
        if (request is null)
        {
            return false;
        }

        FlightContext flight;
        lock (_gate)
        {
            flight = _flight;
        }

        if (!flight.IsActive)
        {
            _ = _arbiter.SpeakAsync("SayIntentions has no active flight.", SpeechPriority.Normal);
            return true;
        }

        _ = TransmitRequestAsync(request, flight, options);
        return true;
    }

    private async Task TransmitRequestAsync(
        AtcRequestDefinition request, FlightContext flight, SayIntentionsOptions options)
    {
        try
        {
            var apiKey = ResolveApiKey(flight, options);
            if (apiKey is null)
            {
                _logger.LogWarning("SayIntentions request skipped — no API key");
                return;
            }

            if (options.AutoTuneFrequency && request.CommType.Length > 0)
            {
                await TryAutoTuneAsync(apiKey, flight, request.CommType).ConfigureAwait(false);
            }

            await Task.Delay(400).ConfigureAwait(false); // radio-clear settle (no-SimVars fallback)
            var message = Fill(PickTemplate(request, options), flight);
            await SayAsAsync(apiKey, message).ConfigureAwait(false);
            _eventLog.Record("sayintentions.request", new { station = request.Station, message });

            if (request.IsDeparture)
            {
                lock (_gate)
                {
                    _departure = DepartureState.Requested;
                }

                await SetVarAsync(apiKey, "SIAI_COPILOT", "1").ConfigureAwait(false);
                lock (_gate)
                {
                    _departure = DepartureState.Restored;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SayIntentions request failed");
        }
    }

    private void PollFlightJson()
    {
        try
        {
            var options = _options.CurrentValue;
            if (!options.Enabled)
            {
                return;
            }

            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SayIntentionsAI", "flight.json");
            var flight = ReadFlightJson(path);

            bool becameActive;
            bool gateTripped;
            lock (_gate)
            {
                becameActive = flight.IsActive && !_wasActive;
                if (!flight.IsActive)
                {
                    _departure = DepartureState.Idle;
                }

                gateTripped = flight.IsActive
                    && _departure == DepartureState.Idle
                    && options.DepartureGatingEnabled
                    && flight.DistanceToRunway <= options.DepartureGateDistanceNm;
                if (gateTripped)
                {
                    _departure = DepartureState.CommsTakenBack;
                }

                _wasActive = flight.IsActive;
                _flight = flight;
            }

            if (becameActive && options.DepartureGatingEnabled)
            {
                // The SI copilot owns comms on the ground and handles readbacks.
                _ = WithApiKey(flight, options, key => SetVarAsync(key, "SIAI_COPILOT", "1"));
            }

            if (gateTripped)
            {
                _ = TakeCommsForDepartureAsync(flight, options);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "flight.json poll failed");
        }
    }

    private async Task TakeCommsForDepartureAsync(FlightContext flight, SayIntentionsOptions options)
    {
        try
        {
            var apiKey = ResolveApiKey(flight, options);
            if (apiKey is null)
            {
                return;
            }

            await SetVarAsync(apiKey, "SIAI_COPILOT", "0").ConfigureAwait(false);
            await TryAutoTuneAsync(apiKey, flight, "TOWER").ConfigureAwait(false);
            await Task.Delay(400).ConfigureAwait(false);
            await SayAsAsync(apiKey, Fill("Contact tower, {callsign}", flight)).ConfigureAwait(false);
            _eventLog.Record("sayintentions.departureGate", new { });
            await _arbiter.EnqueueAsync(new SpeechRequest(
                "Comms are ours — call the takeoff request when ready.", SpeechPriority.Normal,
                Tag: "sayintentions")).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Departure comms gate failed");
        }
    }

    private async Task TryAutoTuneAsync(string apiKey, FlightContext flight, string commType)
    {
        try
        {
            var airport = flight.Airport ?? flight.Origin;
            if (airport is null)
            {
                return;
            }

            using var response = await Http.GetAsync(
                $"{ApiBase}/getWX?api_key={Uri.EscapeDataString(apiKey)}&icao={Uri.EscapeDataString(airport)}&with_comms=1")
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            var freq = FindCommFrequency(doc.RootElement, commType);
            if (freq is not null)
            {
                using var tune = await Http.GetAsync(
                    $"{ApiBase}/setFreq?api_key={Uri.EscapeDataString(apiKey)}&freq={Uri.EscapeDataString(freq)}&com=1&mode=active")
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Auto-tune failed");
        }
    }

    private static string? FindCommFrequency(JsonElement root, string commType)
    {
        // comms[] can sit at root or one level deep.
        foreach (var element in EnumerateSelfAndChildren(root))
        {
            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in element.EnumerateArray())
                {
                    if (entry.ValueKind == JsonValueKind.Object
                        && entry.TryGetProperty("type", out var type)
                        && entry.TryGetProperty("freq", out var freq)
                        && (type.GetString()?.Contains(commType, StringComparison.OrdinalIgnoreCase) ?? false))
                    {
                        return freq.ValueKind == JsonValueKind.String
                            ? freq.GetString()
                            : freq.GetRawText();
                    }
                }
            }
        }

        return null;
    }

    private static IEnumerable<JsonElement> EnumerateSelfAndChildren(JsonElement root)
    {
        yield return root;
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in root.EnumerateObject())
            {
                yield return property.Value;
            }
        }
    }

    private async Task SayAsAsync(string apiKey, string message)
    {
        var truncated = message.Length > 255 ? message[..255] : message;
        using var response = await Http.GetAsync(
            $"{ApiBase}/sayAs?api_key={Uri.EscapeDataString(apiKey)}&channel=COM1&message={Uri.EscapeDataString(truncated)}")
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("sayAs failed: {Status}", (int)response.StatusCode);
        }
    }

    private async Task SetVarAsync(string apiKey, string name, string value)
    {
        using var response = await Http.GetAsync(
            $"{ApiBase}/setVar?api_key={Uri.EscapeDataString(apiKey)}&var={Uri.EscapeDataString(name)}&value={Uri.EscapeDataString(value)}&category=L")
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogDebug("setVar {Name} failed: {Status}", name, (int)response.StatusCode);
        }
    }

    private static async Task WithApiKey(
        FlightContext flight, SayIntentionsOptions options, Func<string, Task> action)
    {
        var key = ResolveApiKey(flight, options);
        if (key is not null)
        {
            await action(key).ConfigureAwait(false);
        }
    }

    private static string? ResolveApiKey(FlightContext flight, SayIntentionsOptions options)
        => options.ApiKeySource.Equals("manual", StringComparison.OrdinalIgnoreCase)
            ? (string.IsNullOrWhiteSpace(options.ManualApiKey) ? null : options.ManualApiKey)
            : flight.ApiKey;

    private static string PickTemplate(AtcRequestDefinition request, SayIntentionsOptions options)
        => options.Phraseology.Equals("faa", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(request.Faa)
            ? request.Faa
            : request.Icao;

    private static string Fill(string template, FlightContext flight)
        => template
            .Replace("{callsign}", flight.Callsign ?? "", StringComparison.Ordinal)
            .Replace("{gate}", flight.Gate ?? "", StringComparison.Ordinal)
            .Replace("{runway}", flight.DepartureRunway ?? "", StringComparison.Ordinal)
            .Replace("{airport}", flight.Airport ?? "", StringComparison.Ordinal)
            .Replace("{origin}", flight.Origin ?? "", StringComparison.Ordinal)
            .Replace("  ", " ", StringComparison.Ordinal)
            .Trim();

    private static FlightContext ReadFlightJson(string path)
    {
        if (!File.Exists(path))
        {
            return FlightContext.Empty;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement.TryGetProperty("flight_details", out var details)
                ? details
                : doc.RootElement;
            var current = root.TryGetProperty("current_flight", out var cf) ? cf : default;

            string? Str(JsonElement e, string name)
                => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
                    ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()
                    : null;

            var callsign = Str(root, "callsign") ?? Str(root, "callsign_icao");
            var distance = double.TryParse(
                Str(root, "distance_to_runway"),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var d) ? d : double.MaxValue;

            return new FlightContext(
                IsActive: callsign is not null,
                ApiKey: Str(root, "api_key"),
                Callsign: callsign,
                Gate: Str(current, "assigned_gate"),
                DepartureRunway: Str(current, "flight_plan_departing_runway"),
                Airport: Str(root, "current_airport"),
                Origin: Str(current, "flight_origin"),
                DistanceToRunway: distance);
        }
        catch (IOException)
        {
            return FlightContext.Empty; // mid-write — keep polling
        }
        catch (JsonException)
        {
            return FlightContext.Empty;
        }
    }

    private void LoadRequests()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "config", "atc-requests.json");
            if (!File.Exists(path))
            {
                return;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("requests", out var requests))
            {
                _requests = JsonSerializer.Deserialize<List<AtcRequestDefinition>>(
                    requests.GetRawText(), ChecklistDefinition.JsonOptions) ?? [];
            }

            _logger.LogInformation("Loaded {Count} SayIntentions ATC requests", _requests.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "atc-requests.json load failed");
        }
    }
}
