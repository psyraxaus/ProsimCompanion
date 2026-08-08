using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NAudio.Wave;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Briefings;
using ProsimCompanion.Speech.Llm;
using ProsimCompanion.Speech.Playback;
using ProsimCompanion.Speech.Recognition;
using ProsimCompanion.Speech.Tts;

namespace ProsimCompanion.Speech;

/// <summary>
/// <see cref="ISpeechDiagnostics"/>: the /speech page's component test buttons. Providers are
/// exercised directly — deliberately outside <c>TtsRouter</c>, so a test never trips the
/// router's failure cooldown and the fallback chain can't mask a broken provider. Results are
/// sentences, not exceptions: a failing dependency is exactly what the user asked about.
/// </summary>
public sealed class SpeechDiagnosticsService : ISpeechDiagnostics
{
    private const int MicSampleRate = 16_000; // same format the recognizer captures
    private static readonly TimeSpan MicCaptureWindow = TimeSpan.FromSeconds(2);

    private readonly IEnumerable<ITtsProvider> _providers;
    private readonly ISpeechPlayback _playback;
    private readonly IOptionsMonitor<SpeechOptions> _options;
    private readonly IOptionsMonitor<BriefingOptions> _briefingOptions;
    private readonly DfdNavDataProvider _navData;
    private readonly OpenAiChatClient _llm;
    private readonly ILogger<SpeechDiagnosticsService> _logger;

    public SpeechDiagnosticsService(
        IEnumerable<ITtsProvider> providers,
        ISpeechPlayback playback,
        IOptionsMonitor<SpeechOptions> options,
        IOptionsMonitor<BriefingOptions> briefingOptions,
        DfdNavDataProvider navData,
        ILogger<SpeechDiagnosticsService> logger,
        OpenAiChatClient? llm = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(playback);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(briefingOptions);
        ArgumentNullException.ThrowIfNull(navData);
        ArgumentNullException.ThrowIfNull(logger);

        _providers = providers;
        _playback = playback;
        _options = options;
        _briefingOptions = briefingOptions;
        _navData = navData;
        _logger = logger;
        _llm = llm ?? new OpenAiChatClient(briefingOptions);
    }

    public Task<string> WakeLlmServerAsync(CancellationToken cancellationToken)
    {
        var wol = _briefingOptions.CurrentValue.LlmWakeOnLan;
        if (string.IsNullOrWhiteSpace(wol.MacAddress))
        {
            return Task.FromResult(
                "No MAC address configured — set it under First Officer → Briefings & LLM → Wake-on-LAN.");
        }

        return Task.FromResult(WakeOnLan.Send(wol.MacAddress, wol.BroadcastAddress, wol.Port, _logger));
    }

    public Task<string> TestNavDataAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Always echo the exact path the app resolved — "set but not working" is nearly
            // always a path the app can't see (typo, unsaved edit, quotes, network drive).
            var path = _navData.ConfiguredPath;
            if (path.Length == 0)
            {
                return Task.FromResult(
                    "No DFD database configured — set the path on App Settings → Nav Data and SAVE. "
                    + "Briefings will speak without nav facts until then.");
            }

            if (!_navData.IsConfigured)
            {
                return Task.FromResult(
                    $"No file found at \"{path}\" — check the path on App Settings → Nav Data.");
            }

            var cycle = _navData.AiracCycle;
            return Task.FromResult(cycle is null
                ? $"File found at \"{path}\" but the AIRAC header could not be read — wrong file or an unsupported schema."
                : $"DFD readable at \"{path}\" — AIRAC cycle {cycle}.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Nav-data test failed");
            return Task.FromResult($"Nav-data test failed: {ex.Message}");
        }
    }

    public async Task<string> TestTtsProviderAsync(
        string providerName, string sampleText, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sampleText);

        var provider = _providers.FirstOrDefault(
            p => p.Name.Equals(providerName, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            return $"Unknown provider \"{providerName}\".";
        }

        if (!provider.IsConfigured)
        {
            return $"{provider.Name} is not configured — nothing to test.";
        }

        if (_options.CurrentValue.LocalOnly && provider.IsNetworkProvider)
        {
            return $"{provider.Name} is excluded by local-only mode.";
        }

        try
        {
            var stopwatch = Stopwatch.StartNew();
            var audio = await provider.SynthesizeAsync(sampleText, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            await _playback.PlayAsync(audio.WavBytes, cancellationToken).ConfigureAwait(false);
            return $"{provider.Name}: synthesized in {stopwatch.ElapsedMilliseconds} ms and played.";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TTS provider test failed for {Provider}", provider.Name);
            return $"{provider.Name} failed: {ex.Message}";
        }
    }

    public async Task<string> TestLlmAsync(CancellationToken cancellationToken)
    {
        if (!_llm.IsConfigured)
        {
            return "LLM styling is disabled or no model is named (briefing.llmEnabled / llmModel) "
                + "— briefings and debriefs use the built-in templates.";
        }

        try
        {
            var stopwatch = Stopwatch.StartNew();
            var reply = await _llm.CompleteAsync(
                "You are a connectivity test. Reply with the single word OK.",
                "ping",
                cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            if (string.IsNullOrWhiteSpace(reply))
            {
                return $"Connected in {stopwatch.ElapsedMilliseconds} ms, but the model returned no content.";
            }

            var shown = reply.Trim();
            if (shown.Length > 80)
            {
                shown = shown[..80] + "…";
            }

            return $"Connected — replied \"{shown}\" in {stopwatch.ElapsedMilliseconds} ms.";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LLM connectivity test failed");
            return $"LLM test failed: {ex.Message}";
        }
    }

    public async Task<string> TestMicrophoneAsync(CancellationToken cancellationToken)
    {
        var configured = _options.CurrentValue.InputDevice;
        float peak = 0;
        string deviceName;
        try
        {
            var deviceNumber = LanAsrRecognizer.ResolveDevice(configured);
            deviceName = WaveInEvent.GetCapabilities(deviceNumber).ProductName;
            using var waveIn = new WaveInEvent
            {
                DeviceNumber = deviceNumber,
                WaveFormat = new WaveFormat(MicSampleRate, 16, 1),
                BufferMilliseconds = 50,
            };
            waveIn.DataAvailable += (_, e) =>
            {
                for (var i = 0; i + 1 < e.BytesRecorded; i += 2)
                {
                    var sample = Math.Abs((int)BitConverter.ToInt16(e.Buffer, i)) / 32768f;
                    if (sample > peak)
                    {
                        peak = sample;
                    }
                }
            };

            waveIn.StartRecording();
            try
            {
                await Task.Delay(MicCaptureWindow, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                waveIn.StopRecording();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Microphone test failed (configured device: {Device})", configured);
            return $"Microphone test failed: {ex.Message}";
        }

        if (peak < 0.01f)
        {
            return $"Captured {MicCaptureWindow.TotalSeconds:F0} s of silence on \"{deviceName}\" "
                + "— check the input device and its Windows level.";
        }

        var peakDb = 20 * Math.Log10(peak);
        return string.Create(CultureInfo.InvariantCulture,
            $"Heard you on \"{deviceName}\" — peak {peakDb:F0} dBFS over {MicCaptureWindow.TotalSeconds:F0} s.");
    }
}
