using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Speech;
using ProsimCompanion.Core.State;
using Windows.Media.SpeechSynthesis;

namespace ProsimCompanion.Speech.Tts;

/// <summary>
/// WinRT synthesis (Windows.Media.SpeechSynthesis) — the only provider that can reach the
/// Windows 11 neural "Natural" voices (System.Speech cannot enumerate them). Always
/// configured: it needs no external service, only a Windows voice. One disposable synthesizer
/// per call. No cache — synthesis is local and fast.
/// </summary>
public sealed class WinRtTtsProvider : ITtsProvider, IVoiceListingTtsProvider
{
    private readonly IOptionsMonitor<SpeechOptions> _options;
    private readonly ILogger<WinRtTtsProvider> _logger;

    public WinRtTtsProvider(IOptionsMonitor<SpeechOptions> options, ILogger<WinRtTtsProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _logger = logger;
    }

    public string Name => "winrt";

    public bool IsConfigured => true;

    public bool IsNetworkProvider => false;

    /// <summary>Every installed Windows voice (incl. the Windows 11 neural "Natural" ones),
    /// by display name — the id the synthesizer above matches on.</summary>
    public Task<IReadOnlyList<VoiceEntry>> ListVoicesAsync(SpeechOptions draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<VoiceEntry> voices = SpeechSynthesizer.AllVoices
            .Select(v => VoiceCatalog.FromWindows("winrt", v.DisplayName, v.Language, v.Gender.ToString()))
            .OrderBy(e => e.Region, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return Task.FromResult(voices);
    }

    public async Task<TtsAudio> SynthesizeAsync(string text, CancellationToken cancellationToken, string? voiceOverride = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        cancellationToken.ThrowIfCancellationRequested();


        using var synthesizer = new SpeechSynthesizer();

        var wanted = string.IsNullOrWhiteSpace(voiceOverride) ? _options.CurrentValue.WinRtVoice : voiceOverride;
        if (!string.IsNullOrWhiteSpace(wanted))
        {
            var voice = SpeechSynthesizer.AllVoices.FirstOrDefault(v =>
                string.Equals(v.DisplayName, wanted, StringComparison.Ordinal)
                || string.Equals(v.Id, wanted, StringComparison.Ordinal));
            if (voice is not null)
            {
                synthesizer.Voice = voice;
            }
            else
            {
                _logger.LogWarning("WinRT voice '{Voice}' not found; using default", wanted);
            }
        }

        using var stream = await synthesizer.SynthesizeTextToStreamAsync(text)
            .AsTask(cancellationToken).ConfigureAwait(false);

        using var wavStream = stream.AsStreamForRead();
        using var buffer = new MemoryStream();
        await wavStream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

        var wav = buffer.ToArray();
        if (wav.Length == 0)
        {
            throw new InvalidOperationException("WinRT TTS returned empty audio");
        }

        return new TtsAudio(wav, Name);
    }
}
