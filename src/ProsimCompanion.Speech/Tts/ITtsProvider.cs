namespace ProsimCompanion.Speech.Tts;

/// <summary>Synthesized speech, always as a complete RIFF/WAV container so the playback layer
/// never needs per-provider format knowledge (providers that produce raw PCM wrap it).</summary>
public sealed record TtsAudio(byte[] WavBytes, string ProviderName);

/// <summary>
/// One synthesis backend in the router chain (docs/integrations/speech.md: Kokoro → Google →
/// WinRT → SAPI5). Implementations throw on failure — the router owns fallback, cooldown and
/// health reporting; providers stay dumb.
/// </summary>
public interface ITtsProvider
{
    /// <summary>Stable name for logs and the /speech page ("kokoro", "google", "winrt", "sapi").</summary>
    string Name { get; }

    /// <summary>False when unconfigured (no URL/key/voice) — the router skips without
    /// counting a failure.</summary>
    bool IsConfigured { get; }

    /// <summary>True for providers that leave the machine — excluded in local-only mode.</summary>
    bool IsNetworkProvider { get; }

    /// <summary>Synthesizes the text, honouring the token promptly — pre-emption cancels
    /// synthesis in flight. <paramref name="voiceOverride"/> selects a speaker-role voice for
    /// this one call (id in the provider's own vocabulary); null means the provider's
    /// configured First Officer voice. Providers with per-voice caches MUST namespace on the
    /// effective voice; providers without configurable voices may ignore the override (logged,
    /// not an error).</summary>
    Task<TtsAudio> SynthesizeAsync(string text, CancellationToken cancellationToken, string? voiceOverride = null);
}
