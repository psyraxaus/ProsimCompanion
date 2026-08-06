using Microsoft.Extensions.DependencyInjection;
using ProsimCompanion.Audio.Acp;
using ProsimCompanion.Audio.Backends.CoreAudio;
using ProsimCompanion.Audio.Backends.VoiceMeeter;
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

        return services;
    }
}
