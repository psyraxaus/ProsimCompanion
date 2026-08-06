using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Speech.Tts;

/// <summary>
/// SAPI5 last resort via System.Speech — the voices every Windows install has. The API is
/// synchronous, so it runs on the thread pool; the token only prevents scheduling — an
/// in-flight Speak() is not interruptible (acceptable for a provider of last resort).
/// </summary>
public sealed class Sapi5TtsProvider : ITtsProvider
{
    private readonly IOptionsMonitor<SpeechOptions> _options;
    private readonly ILogger<Sapi5TtsProvider> _logger;

    public Sapi5TtsProvider(IOptionsMonitor<SpeechOptions> options, ILogger<Sapi5TtsProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _logger = logger;
    }

    public string Name => "sapi";

    public bool IsConfigured => true;

    public bool IsNetworkProvider => false;

    public Task<TtsAudio> SynthesizeAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        return Task.Run(() =>
        {
            using var synthesizer = new System.Speech.Synthesis.SpeechSynthesizer();

            var wanted = _options.CurrentValue.SapiVoice;
            if (!string.IsNullOrWhiteSpace(wanted))
            {
                try
                {
                    synthesizer.SelectVoice(wanted);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("SAPI5 voice '{Voice}' unavailable ({Message}); using default",
                        wanted, ex.Message);
                }
            }

            using var buffer = new MemoryStream();
            synthesizer.SetOutputToWaveStream(buffer);
            synthesizer.Speak(text);

            var wav = buffer.ToArray();
            if (wav.Length == 0)
            {
                throw new InvalidOperationException("SAPI5 TTS returned empty audio");
            }

            return new TtsAudio(wav, Name);
        }, cancellationToken);
    }
}
