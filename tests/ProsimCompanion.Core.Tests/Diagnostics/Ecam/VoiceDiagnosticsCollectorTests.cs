using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Diagnostics.Ecam;
using ProsimCompanion.Core.Diagnostics.Metrics;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Diagnostics;
using ProsimCompanion.Speech.Llm;
using ProsimCompanion.Speech.Tts;
using Xunit;

namespace ProsimCompanion.Core.Tests.Diagnostics.Ecam;

/// <summary>The VOICE collector over faked stores: the normal pipeline, the LLM-host-asleep
/// caution with its WoL action, TTS reconnecting, and the empty-stores case; plus the latency
/// hooks in the LLM client and the TTS router (record on success, not on failure).</summary>
public sealed class VoiceDiagnosticsCollectorTests
{
    private readonly ConnectionStatusStore _connections = new();
    private readonly SpeechStatusStore _speech = new();
    private readonly VoiceSignalStore _signals = new();
    private readonly LlmHealthStore _llm = new();
    private readonly LatencyRegistry _latency = new();

    private VoiceDiagnosticsCollector Collector(
        BriefingOptions? briefing = null,
        ILlmHostView? probe = null,
        IWakeOnLanControl? wake = null,
        TtsRouter? router = null,
        DiagnosticsOptions? diagnostics = null)
        => new(
            _connections, _speech, _signals, _llm, _latency,
            new FixedOptionsMonitor<SpeechOptions>(new SpeechOptions { AsrBaseUrl = "http://10.0.1.30:8000", KokoroBaseUrl = "http://10.0.1.30:8880" }),
            new FixedOptionsMonitor<BriefingOptions>(briefing ?? new BriefingOptions { LlmEnabled = true, LlmModel = "qwen3", LlmBaseUrl = "http://10.0.1.23:11434" }),
            new FixedOptionsMonitor<DiagnosticsOptions>(diagnostics ?? new DiagnosticsOptions()),
            probe, null, router, wake);

    [Fact]
    public void EmptyStores_EveryPipelineNodeIsNoDataOrOff_AndNothingThrows()
    {
        var (page, cautions, _) = CollectorTestSupport.Run(Collector(new BriefingOptions()));

        Assert.Equal(DiagState.NoData, page.Nodes["mic"].State);
        Assert.Equal(DiagState.NoData, page.Nodes["vad"].State);
        Assert.Equal(DiagState.NotApplicable, page.Nodes["stt"].State);   // ASR dot never set = Disabled
        Assert.Equal(DiagState.NotApplicable, page.Nodes["llm"].State);   // LLM not configured
        Assert.Equal(DiagState.NotApplicable, page.Nodes["tts"].State);
        Assert.Equal(EcamTokens.Idle, page.Nodes["out"].Value);
        Assert.Null(page.GaugeOrNoData("e2e").Value);
        Assert.Empty(cautions.Cautions);
        Assert.Equal(5, page.Links.Count);
    }

    [Fact]
    public void NormalPipeline_GreenNodes_WithLatenciesHostsAndLastUtterance()
    {
        _connections.Set(Subsystems.Asr, ConnectionState.Connected);
        _connections.Set(Subsystems.Tts, ConnectionState.Connected);
        _llm.Report(LlmHealthState.Healthy);
        _latency[LatencyNames.Asr].Record(412);
        _latency[LatencyNames.Tts].Record(86);
        _latency[LatencyNames.EndToEnd].Record(1840);
        _latency[LatencyNames.LlmTokensPerSecond].Record(63);
        _signals.Signal(-38, 16_000, false, "silero");
        _signals.UtteranceClosed(1300);
        _signals.Accepted("set flaps one", 0.94, EcamTestSupport.T0);
        _signals.Decided("Command", "flaps one");
        _speech.Update(s => s with { QueueDepth = 0, NowPlaying = "Flaps one." });

        var labels = new DiagnosticsOptions();
        labels.EcamHostLabels["10.0.1.30"] = "Mac mini";
        var (page, cautions, _) = CollectorTestSupport.Run(Collector(diagnostics: labels));

        Assert.Equal("-38 dB", page.Nodes["mic"].Value);
        Assert.Equal("16 kHz", page.Nodes["mic"].Detail);
        Assert.Equal(EcamTokens.Silence, page.Nodes["vad"].Value);
        Assert.Equal("412 ms", page.Nodes["stt"].Value);
        Assert.Equal("MAC MINI", page.Nodes["stt"].Host);
        Assert.Equal("10.0.1.23", page.Nodes["llm"].Host);
        Assert.Equal(DiagState.Normal, page.Nodes["llm"].State);
        Assert.Equal("86 ms", page.Nodes["tts"].Value);
        Assert.Equal(EcamTokens.Playing, page.Nodes["out"].Value);
        Assert.Equal(1840, page.Gauges["e2e"].Value);
        Assert.Equal("63", page.Readout("tok_s"));
        Assert.Equal("0.32", page.Readout("rtf"));
        Assert.Equal("\"SET FLAPS ONE\"", page.Readout("utt_text"));
        Assert.Equal("0.94", page.Readout("utt_conf"));
        Assert.Equal("1.3", page.Readout("utt_dur"));
        Assert.Equal("COMMAND", page.Readout("intent"));
        Assert.Equal("FLAPS ONE", page.Readout("action"));
        Assert.Equal("10.0.1.30:8000", page.Readout("ep_stt"));
        Assert.Equal("10.0.1.23:11434", page.Readout("ep_llm"));
        Assert.All(page.Links, l => Assert.Equal(DiagState.Normal, l.State));
        Assert.Empty(cautions.Cautions);
        Assert.Null(page.CautionLine);
    }

    [Fact]
    public void LlmUnreachable_HostDown_IsACautionWithWolAndInop()
    {
        _llm.Report(LlmHealthState.Unreachable, "connection refused");
        var wake = new Mock<IWakeOnLanControl>();
        wake.SetupGet(w => w.LastSentUtc).Returns(EcamTestSupport.T0.AddMinutes(-2));

        var collector = Collector(probe: new HostDownProbe(), wake: wake.Object);
        var (page, cautions, _) = CollectorTestSupport.Run(collector);

        Assert.Equal(DiagState.Caution, page.Nodes["llm"].State);
        Assert.Equal("HOST DOWN", page.Nodes["llm"].Detail);
        var caution = Assert.Single(cautions.Cautions, c => c.Title == "LLM HOST NOT RESPONDING");
        Assert.Equal(DiagState.Caution, caution.Severity);
        Assert.Contains(caution.Actions, a => a.Label == "WOL MAGIC PKT" && a.Value.StartsWith("SENT", StringComparison.Ordinal));
        Assert.Contains("LLM BRIEFING", cautions.InopSystems);
        Assert.Equal("LLM HOST SLEEPING · WOL SENT", page.CautionLine);
        Assert.Equal(DiagState.Caution, page.LinkState("stt", "llm"));
    }

    [Fact]
    public void LlmUnreachable_HostUp_IsAWarning()
    {
        _llm.Report(LlmHealthState.Unreachable, "refused");
        var (page, cautions, _) = CollectorTestSupport.Run(Collector(probe: new HostUpProbe()));

        Assert.Equal(DiagState.Warning, page.Nodes["llm"].State);
        Assert.Single(cautions.Cautions, c => c.Title == "LLM SERVER DOWN" && c.Severity == DiagState.Warning);
    }

    [Fact]
    public void TtsReconnecting_IsACautionWithAutoReconnect()
    {
        _connections.Set(Subsystems.Tts, ConnectionState.Disconnected);
        var (page, cautions, log) = CollectorTestSupport.Run(Collector(new BriefingOptions()));

        Assert.Equal(DiagState.Caution, page.Nodes["tts"].State);
        var caution = Assert.Single(cautions.Cautions);
        Assert.Equal("TTS SERVER NOT RESPONDING", caution.Title);
        Assert.Equal(EcamTokens.Auto, Assert.Single(caution.Actions).Value);
        Assert.Contains("FO VOICE", cautions.DegradedSystems);
        Assert.Contains(log.Snapshot(), e => e.Text == "TTS DEGRADED" && e.State == DiagState.Caution);
    }

    [Fact]
    public async Task LlmClient_RecordsFirstTokenAndTotal_OnSuccess_NotOnFailure()
    {
        var options = new FixedOptionsMonitor<BriefingOptions>(new BriefingOptions
        {
            LlmEnabled = true, LlmModel = "m", LlmBaseUrl = "http://llm.invalid/v1", LlmTimeoutSeconds = 5,
        });
        var ok = new OpenAiChatClient(options, new HttpClient(new SseHandler(
            "data: {\"choices\":[{\"delta\":{\"content\":\"Hello\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\" there\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\".\"}}]}\n\n" +
            "data: [DONE]\n\n")), null, _latency);

        var text = new StringBuilder();
        await foreach (var delta in ok.StreamAsync("sys", "usr"))
        {
            text.Append(delta);
        }

        Assert.Equal("Hello there.", text.ToString());
        Assert.Equal(1, _latency[LatencyNames.LlmFirstToken].Count);
        Assert.Equal(1, _latency[LatencyNames.LlmTotal].Count);

        var failing = new OpenAiChatClient(options, new HttpClient(new SseHandler(null)), null, _latency);
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await foreach (var _ in failing.StreamAsync("sys", "usr"))
            {
            }
        });

        Assert.Equal(1, _latency[LatencyNames.LlmFirstToken].Count);
        Assert.Equal(1, _latency[LatencyNames.LlmTotal].Count);
    }

    [Fact]
    public async Task TtsRouter_RecordsTts_OnSuccess_NotOnFailure()
    {
        var good = new FakeProvider("kokoro", fail: false);
        var router = new TtsRouter([good], new FixedOptionsMonitor<SpeechOptions>(new SpeechOptions()),
            _speech, NullLogger<TtsRouter>.Instance, null, _latency);
        await router.SynthesizeAsync("v one", CancellationToken.None, "af_heart");
        Assert.Equal(1, _latency[LatencyNames.Tts].Count);
        Assert.Equal("af_heart", router.LastSynthesis?.Voice);

        var bad = new FakeProvider("kokoro", fail: true);
        var failing = new TtsRouter([bad], new FixedOptionsMonitor<SpeechOptions>(new SpeechOptions()),
            _speech, NullLogger<TtsRouter>.Instance, null, _latency);
        Assert.Null(await failing.SynthesizeAsync("v one", CancellationToken.None));
        Assert.Equal(1, _latency[LatencyNames.Tts].Count);
    }

    [Fact]
    public void VoiceTurnTracker_MeasuresUtteranceEndToFirstAudio_AndIgnoresRejectedTurns()
    {
        var turns = new VoiceTurnTracker(_latency);
        Assert.Null(turns.AudioStarted(EcamTestSupport.T0));

        turns.UtteranceEnded(EcamTestSupport.T0);
        Assert.True(turns.TurnOpen);
        Assert.Equal(1840, turns.AudioStarted(EcamTestSupport.T0.AddMilliseconds(1840)));
        Assert.False(turns.TurnOpen);
        Assert.Equal(1840, _latency[LatencyNames.EndToEnd].Last);

        turns.UtteranceEnded(EcamTestSupport.T0.AddSeconds(10));
        turns.UtteranceRejected();
        Assert.Null(turns.AudioStarted(EcamTestSupport.T0.AddSeconds(11)));

        turns.UtteranceEnded(EcamTestSupport.T0.AddSeconds(20));
        Assert.Null(turns.AudioStarted(EcamTestSupport.T0.AddSeconds(50))); // outside the window
        Assert.Equal(1, _latency[LatencyNames.EndToEnd].Count);
    }

    [Fact]
    public void LlmHostProbe_ParsesEndpointsAndLlamaMetrics()
    {
        Assert.Equal(("10.0.1.23", 11434), LlmHostProbe.Endpoint("http://10.0.1.23:11434/v1"));
        Assert.Equal(("llm.local", 80), LlmHostProbe.Endpoint("http://llm.local/api"));
        Assert.Null(LlmHostProbe.Endpoint("not a url"));
        Assert.Null(LlmHostProbe.Endpoint(""));

        var view = LlmHostProbe.Apply(LlmHostView.Empty, "# HELP x\nllamacpp:kv_cache_usage_ratio 0.4375\nllamacpp:prompt_tokens_total 12\n");
        Assert.Equal(44, view.KvCacheUsagePercent);
    }

    [Fact]
    public void HostLabel_UsesTheUsersName_ElseTheAddress()
    {
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["10.0.1.30"] = "Mac mini" };
        Assert.Equal("MAC MINI", VoiceDiagnosticsCollector.HostLabel(labels, "10.0.1.30"));
        Assert.Equal("10.0.1.23", VoiceDiagnosticsCollector.HostLabel(labels, "10.0.1.23"));
        Assert.Null(VoiceDiagnosticsCollector.HostLabel(labels, null));
    }

    private sealed class HostDownProbe : ILlmHostView
    {
        public LlmHostView View { get; } = LlmHostView.Empty with { Host = "10.0.1.23", Port = 11434, HostReachable = false, ConsecutiveFailures = 3 };
    }

    private sealed class HostUpProbe : ILlmHostView
    {
        public LlmHostView View { get; } = LlmHostView.Empty with { Host = "10.0.1.23", Port = 11434, HostReachable = true };
    }

    private sealed class SseHandler(string? body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (body is null)
            {
                throw new HttpRequestException("refused");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
            });
        }
    }

    private sealed class FakeProvider(string name, bool fail) : ITtsProvider
    {
        public string Name => name;
        public bool IsConfigured => true;
        public bool IsNetworkProvider => false;

        public Task<TtsAudio> SynthesizeAsync(string text, CancellationToken cancellationToken, string? voiceOverride = null)
            => fail ? throw new HttpRequestException("down") : Task.FromResult(new TtsAudio([1, 2], name));
    }
}
