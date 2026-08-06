using Microsoft.Extensions.DependencyInjection;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Callouts;
using ProsimCompanion.Speech.Monitoring;
using ProsimCompanion.Speech.Playback;
using ProsimCompanion.Speech.Tts;

namespace ProsimCompanion.Speech;

public static class SpeechServiceCollectionExtensions
{
    /// <summary>Registers the voice First Officer pillar's speech foundations: arbiter, TTS
    /// router and playback. Provider registration order IS the fallback chain order —
    /// Kokoro → Google → WinRT → SAPI5 (docs/integrations/speech.md).</summary>
    public static IServiceCollection AddSpeechServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<TtsDiskCache>();
        services.AddSingleton<TtsUsageTracker>();
        services.AddSingleton<ITtsProvider, KokoroTtsProvider>();
        services.AddSingleton<ITtsProvider, GoogleTtsProvider>();
        services.AddSingleton<ITtsProvider, WinRtTtsProvider>();
        services.AddSingleton<ITtsProvider, Sapi5TtsProvider>();
        services.AddSingleton<TtsRouter>();
        services.AddSingleton<ISpeechPlayback, SpeechPlayback>();
        services.AddSingleton<SpeechArbiterService>();
        services.AddSingleton<ISpeechArbiter>(p => p.GetRequiredService<SpeechArbiterService>());
        services.AddSingleton<ISpeechControl>(p => p.GetRequiredService<SpeechArbiterService>());
        services.AddSingleton<CalloutsEngine>();
        services.AddSingleton<StabilizedApproachMonitor>();
        services.AddHostedService<SpeechBootstrapService>();

        return services;
    }
}
