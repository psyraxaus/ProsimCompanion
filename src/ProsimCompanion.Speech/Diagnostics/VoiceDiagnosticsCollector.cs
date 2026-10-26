using System.Globalization;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Diagnostics.Ecam;
using ProsimCompanion.Core.Diagnostics.Metrics;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Llm;
using ProsimCompanion.Speech.Tts;

namespace ProsimCompanion.Speech.Diagnostics;

/// <summary>
/// The VOICE page: the pilot-to-First-Officer pipeline as six nodes (mic, gate, STT, LLM, TTS,
/// out), the two dials, the readouts, the last utterance and the endpoint bus. Reads the
/// stores the speech pillar already publishes plus the latency registry; the only thing it
/// decides is what "degraded" means for each hop:
/// <list type="bullet">
/// <item>STT / TTS: the footer dot (<see cref="ConnectionStatusStore"/>). Disconnected = caution
/// with an automatic reconnect; Disabled = not applicable.</item>
/// <item>LLM: <see cref="LlmHealthStore"/> crossed with the host probe. Unreachable with the
/// host down = caution (asleep, a wake is the fix); unreachable with the host up = warning
/// (the server is down, a wake will not help); 401 = warning.</item>
/// </list>
/// </summary>
public sealed class VoiceDiagnosticsCollector : IDiagnosticsCollector
{
    /// <summary>End-to-end dial thresholds in ms (a pilot notices a two-and-a-half second pause).</summary>
    public const double E2eCautionMs = 2500;
    public const double E2eWarningMs = 4000;

    private const string System = "VOICE";

    private readonly ConnectionStatusStore _connections;
    private readonly SpeechStatusStore _speech;
    private readonly VoiceSignalStore _signals;
    private readonly LlmHealthStore _llmHealth;
    private readonly ILlmHostView? _hostProbe;
    private readonly LlmHealthProbeService? _healthProbe;
    private readonly LatencyRegistry _latency;
    private readonly TtsRouter? _tts;
    private readonly IWakeOnLanControl? _wake;
    private readonly IOptionsMonitor<SpeechOptions> _speechOptions;
    private readonly IOptionsMonitor<BriefingOptions> _briefing;
    private readonly IOptionsMonitor<DiagnosticsOptions> _diagnostics;
    private readonly StateTransitions<string> _edges = new();

    public VoiceDiagnosticsCollector(
        ConnectionStatusStore connections,
        SpeechStatusStore speech,
        VoiceSignalStore signals,
        LlmHealthStore llmHealth,
        LatencyRegistry latency,
        IOptionsMonitor<SpeechOptions> speechOptions,
        IOptionsMonitor<BriefingOptions> briefing,
        IOptionsMonitor<DiagnosticsOptions> diagnostics,
        ILlmHostView? hostProbe = null,
        LlmHealthProbeService? healthProbe = null,
        TtsRouter? tts = null,
        IWakeOnLanControl? wake = null)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(speech);
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(llmHealth);
        ArgumentNullException.ThrowIfNull(latency);
        ArgumentNullException.ThrowIfNull(speechOptions);
        ArgumentNullException.ThrowIfNull(briefing);
        ArgumentNullException.ThrowIfNull(diagnostics);
        _connections = connections;
        _speech = speech;
        _signals = signals;
        _llmHealth = llmHealth;
        _latency = latency;
        _speechOptions = speechOptions;
        _briefing = briefing;
        _diagnostics = diagnostics;
        _hostProbe = hostProbe;
        _healthProbe = healthProbe;
        _tts = tts;
        _wake = wake;
    }

    public string Page => DiagPageIds.Voice;

    public void Contribute(DiagPageBuilder page, DiagCautionSink cautions)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(cautions);

        var now = page.Events.Now;
        var speech = _speech.Snapshot();
        var signal = _signals.Snapshot();
        var options = _speechOptions.CurrentValue;
        var briefing = _briefing.CurrentValue;
        var labels = _diagnostics.CurrentValue.EcamHostLabels;
        var connections = _connections.Snapshot().ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

        var localHost = HostLabel(labels, "localhost");
        var sttHost = HostOf(options.AsrBaseUrl);
        var llmHost = HostOf(briefing.LlmBaseUrl);
        var ttsHost = HostOf(options.KokoroBaseUrl);

        // mic + vad
        if (signal.MicLevelDb is { } db)
        {
            page.Node("mic", DiagState.Normal, $"{db.ToString("0", CultureInfo.InvariantCulture)} dB",
                signal.SampleRateHz is { } hz ? $"{hz / 1000} kHz" : null, localHost);
            page.Node("vad", DiagState.Normal, signal.InSpeech ? EcamTokens.Speech : EcamTokens.Silence,
                signal.VadEngine.ToUpperInvariant(), localHost);
        }
        else
        {
            var paused = speech.ListeningPaused;
            page.Node("mic", paused ? DiagState.NotApplicable : DiagState.NoData, paused ? EcamTokens.NotApplicable : EcamTokens.NoData,
                paused ? "PAUSED" : "NOT LISTENING", localHost);
            page.NoData("vad", signal.VadEngine.Length > 0 ? signal.VadEngine.ToUpperInvariant() : null, localHost);
        }

        // stt
        var asr = connections.GetValueOrDefault(Subsystems.Asr, ConnectionState.Disabled);
        var asrMs = _latency[LatencyNames.Asr].Last;
        var sttState = LinkState(asr);
        page.Node("stt", sttState,
            sttState == DiagState.NotApplicable ? EcamTokens.NotApplicable : asrMs is { } a ? $"{a:0} ms" : EcamTokens.NoData,
            asr == ConnectionState.Disabled ? "OFFLINE ENGINE" : speech.RecognizerEngine.Length > 0 ? speech.RecognizerEngine.ToUpperInvariant() : options.AsrApi.ToString().ToUpperInvariant(),
            HostLabel(labels, sttHost));
        if (asr == ConnectionState.Disconnected)
        {
            cautions.Raise(System, "STT SERVER NOT RESPONDING", DiagState.Caution, new DiagAction("RECONNECT", EcamTokens.Auto));
            cautions.Degraded("VOICE RECOGNITION");
            page.CautionLine("STT OFFLINE · OFFLINE ENGINE COVERING");
        }

        // llm
        var health = _llmHealth.Snapshot();
        var host = _hostProbe?.View ?? LlmHostView.Empty;
        var (llmState, llmValue, llmDetail) = LlmNode(briefing, health, host, _latency[LatencyNames.LlmFirstToken].Last);
        page.Node("llm", llmState, llmValue, llmDetail, HostLabel(labels, llmHost));
        if (briefing.LlmEnabled)
        {
            if (health.State == LlmHealthState.Unreachable && host.HostReachable == false)
            {
                cautions.Raise(System, "LLM HOST NOT RESPONDING", DiagState.Caution,
                    new DiagAction("WOL MAGIC PKT", _wake?.LastSentUtc is { } sent ? $"{EcamTokens.Sent} {sent.ToLocalTime():HH:mm}" : "NOT SENT"),
                    new DiagAction("RETRY", _healthProbe is { } p ? $"{p.ConsecutiveFailures} / {p.ProbeCount}" : EcamTokens.NoData),
                    new DiagAction("BRIEFING", EcamTokens.Unavail));
                cautions.Inop("LLM BRIEFING");
                cautions.Inop("FO QUESTIONS");
                page.CautionLine(_wake?.LastSentUtc is not null ? "LLM HOST SLEEPING · WOL SENT" : "LLM HOST NOT RESPONDING");
            }
            else if (health.State == LlmHealthState.Unreachable)
            {
                cautions.Raise(System, "LLM SERVER DOWN", DiagState.Warning,
                    new DiagAction("HOST", host.HostReachable == true ? "UP" : EcamTokens.NoData),
                    new DiagAction("RETRY", _healthProbe is { } p ? $"{p.ConsecutiveFailures} / {p.ProbeCount}" : EcamTokens.NoData));
                cautions.Inop("LLM BRIEFING");
                cautions.Inop("FO QUESTIONS");
                page.CautionLine("LLM SERVER DOWN · HOST UP");
            }
            else if (health.State == LlmHealthState.AuthFailed)
            {
                cautions.Raise(System, "LLM AUTH REJECTED", DiagState.Warning, new DiagAction("API KEY", "CHECK"));
                cautions.Inop("LLM BRIEFING");
                page.CautionLine("LLM AUTH REJECTED");
            }
        }

        // tts
        var tts = connections.GetValueOrDefault(Subsystems.Tts, ConnectionState.Disabled);
        var ttsMs = _latency[LatencyNames.Tts].Last;
        var last = _tts?.LastSynthesis;
        var ttsState = LinkState(tts);
        var ttsValue = ttsState == DiagState.NotApplicable ? EcamTokens.NotApplicable : ttsMs is { } t ? $"{t:0} ms" : EcamTokens.NoData;
        if (tts == ConnectionState.Disabled && last is not null)
        {
            // Local-only voices still speak; the network dot is just off.
            ttsState = DiagState.Normal;
            ttsValue = ttsMs is { } t2 ? $"{t2:0} ms" : EcamTokens.NoData;
        }

        page.Node("tts", ttsState, ttsValue,
            last is null ? ActiveProvider(speech) : (last.Voice ?? last.Provider).ToUpperInvariant(),
            HostLabel(labels, ttsHost));
        if (tts == ConnectionState.Disconnected)
        {
            cautions.Raise(System, "TTS SERVER NOT RESPONDING", DiagState.Caution, new DiagAction("RECONNECT", EcamTokens.Auto));
            cautions.Degraded("FO VOICE");
            page.CautionLine("TTS OFFLINE · FALLBACK VOICE");
        }

        // out
        page.Node("out", DiagState.Normal, speech.NowPlaying is not null ? EcamTokens.Playing : EcamTokens.Idle,
            string.IsNullOrWhiteSpace(options.OutputDevice) ? "DEFAULT DEVICE" : options.OutputDevice.ToUpperInvariant(), localHost);

        page.Link("mic", "vad").Link("vad", "stt").Link("stt", "llm").Link("llm", "tts").Link("tts", "out");

        // dials + readouts
        page.Gauge("e2e", _latency[LatencyNames.EndToEnd].Last, 0, 5000, E2eCautionMs, E2eWarningMs, EcamTokens.Units.Ms);
        page.Gauge("gpu", null, 0, 100, 90, 98, EcamTokens.Units.Percent);
        page.Readout("vram", host.VramGb is { } gb ? gb.ToString("0.0", CultureInfo.InvariantCulture) : null);
        page.Readout("kv", host.KvCacheUsagePercent is { } kv ? kv.ToString("0", CultureInfo.InvariantCulture) : null);
        page.Readout("tok_s", _latency[LatencyNames.LlmTokensPerSecond].Last, "0");
        if (asrMs is { } asrLast && signal.LastDurationMs is { } dur && dur > 0)
        {
            page.Readout("rtf", (asrLast / dur).ToString("0.00", CultureInfo.InvariantCulture));
        }

        page.Readout("queue", speech.QueueDepth.ToString(CultureInfo.InvariantCulture));

        // last utterance
        page.Readout("utt_text", signal.LastText.Length > 0 ? $"\"{signal.LastText.ToUpperInvariant()}\"" : null);
        page.Readout("utt_conf", signal.LastConfidence is { } conf ? conf.ToString("0.00", CultureInfo.InvariantCulture) : null);
        page.Readout("utt_dur", signal.LastDurationMs is { } ms ? (ms / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) : null);
        page.Readout("intent", signal.LastIntent.Length > 0 ? signal.LastIntent.ToUpperInvariant() : null);
        page.Readout("action", signal.LastAction.Length > 0 ? signal.LastAction.ToUpperInvariant() : null);
        page.Readout("gate", signal.VadEngine.Length > 0 ? signal.VadEngine.ToUpperInvariant() : null);
        page.Readout("bleed", _signals.RejectsWithin(now).ToString(CultureInfo.InvariantCulture));

        // endpoint bus
        page.Readout("ep_stt", HostPort(options.AsrBaseUrl));
        page.Readout("ep_llm", HostPort(briefing.LlmBaseUrl));
        page.Readout("ep_tts", HostPort(options.KokoroBaseUrl));

        // RECENT edges
        foreach (var (id, _, to) in _edges.Observe(
        [
            new("STT", sttState), new("LLM", llmState), new("TTS", ttsState),
        ]))
        {
            page.Events.Push($"{id} {StateWord(to)}", to);
        }
    }

    private static (DiagState State, string Value, string? Detail) LlmNode(BriefingOptions briefing, LlmHealthSnapshot health, LlmHostView host, double? firstTokenMs)
    {
        if (!briefing.LlmEnabled || string.IsNullOrWhiteSpace(briefing.LlmModel))
        {
            return (DiagState.NotApplicable, EcamTokens.NotApplicable, "NOT CONFIGURED");
        }

        var backend = briefing.LlmApi == LlmApiKind.Ollama ? "OLLAMA" : "OPENAI API";
        return health.State switch
        {
            LlmHealthState.Healthy => (DiagState.Normal, FirstToken(), backend),
            LlmHealthState.AuthFailed => (DiagState.Warning, "401", backend),
            LlmHealthState.Unreachable when host.HostReachable == false => (DiagState.Caution, EcamTokens.NoData, "HOST DOWN"),
            LlmHealthState.Unreachable => (DiagState.Warning, EcamTokens.NoData, EcamTokens.NoResp),
            _ => (host.HostReachable == true ? DiagState.Normal : DiagState.NoData, FirstToken(), backend),
        };

        // The last first-token time once a stream has run; "- -" (ready, not yet asked) before.
        string FirstToken() => firstTokenMs is { } ms ? $"{ms:0} ms" : "- -";
    }

    /// <summary>Connection state to node state: on = green, retrying = amber, off = not applicable.</summary>
    public static DiagState LinkState(ConnectionState state) => state switch
    {
        ConnectionState.Connected => DiagState.Normal,
        ConnectionState.Connecting => DiagState.Caution,
        ConnectionState.Disconnected => DiagState.Caution,
        _ => DiagState.NotApplicable,
    };

    private static string ActiveProvider(SpeechStatusSnapshot speech)
        => speech.Providers.FirstOrDefault(p => p.Health == TtsProviderHealth.Healthy)?.Name.ToUpperInvariant() ?? "NO VOICE";

    private static string StateWord(DiagState state) => state switch
    {
        DiagState.Normal => "OK",
        DiagState.Caution => "DEGRADED",
        DiagState.Warning => "FAIL",
        DiagState.NotApplicable => "OFF",
        _ => "NO DATA",
    };

    /// <summary>The host part of a URL, or null.</summary>
    public static string? HostOf(string? url)
        => Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? uri.Host : null;

    /// <summary>host:port of a URL for the endpoint bus, or null.</summary>
    public static string? HostPort(string? url)
        => Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? $"{uri.Host}:{uri.Port}" : null;

    /// <summary>The label above a node: the user's name for the host, else the host itself,
    /// upper-cased the ECAM way. Nothing is assumed about which machine runs what.</summary>
    public static string? HostLabel(IReadOnlyDictionary<string, string> labels, string? host)
    {
        if (host is null)
        {
            return null;
        }

        return labels.TryGetValue(host, out var label) && !string.IsNullOrWhiteSpace(label)
            ? label.Trim().ToUpperInvariant()
            : host.ToUpperInvariant();
    }
}
