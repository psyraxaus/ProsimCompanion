using Microsoft.Extensions.DependencyInjection;
using ProsimCompanion.Audio.Acp;
using ProsimCompanion.Audio.Backends.CoreAudio;
using ProsimCompanion.Audio.Backends.VoiceMeeter;
using ProsimCompanion.Audio.Mixer;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Audio;

public static class AudioServiceCollectionExtensions
{
    /// <summary>Registers the cockpit audio-control pillar (ACP knobs/latches → CoreAudio
    /// per-app volumes or VoiceMeeter strips/buses).</summary>
    public static IServiceCollection AddAudioServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<AcpChannelFeed>();
        services.AddSingleton<CoreAudioDeviceRegistry>();
        services.AddSingleton<CoreAudioBackend>();
        services.AddSingleton<VoiceMeeterRemote>();
        services.AddSingleton<VoiceMeeterBinder>();
        services.AddSingleton<ProsimNativeAudioGuard>();
        services.AddSingleton<AudioControlService>();
        services.AddSingleton<IAudioControl>(p => p.GetRequiredService<AudioControlService>());
        services.AddHostedService<AudioBootstrapService>();

        // Remote mixer (VoicemeeterBridge on another PC, 2026-10-10). Both services sleep
        // while mixer.enabled is false — no socket, no dataref subscriptions.
        services.AddSingleton<MixerClient>();
        services.AddSingleton<IMixerClient>(p => p.GetRequiredService<MixerClient>());
        services.AddHostedService(p => p.GetRequiredService<MixerClient>());
        services.AddHostedService<MixerMappingService>();

        return services;
    }
}
