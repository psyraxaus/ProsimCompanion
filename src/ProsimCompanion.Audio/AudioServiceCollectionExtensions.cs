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

        // Remote mixer (VoicemeeterBridge on other PCs, 2026-10-10; one client per agent since
        // #178). Both services sleep while mixer.enabled is false — no socket, no dataref
        // subscriptions. The host owns the per-agent clients and is the app's IMixerClients.
        services.AddSingleton<MixerClientHost>();
        services.AddSingleton<IMixerClients>(p => p.GetRequiredService<MixerClientHost>());
        services.AddHostedService(p => p.GetRequiredService<MixerClientHost>());
        services.AddHostedService<MixerMappingService>();
        // Diagnostics REMOTE page (ADR-0016): mixer agents and the audio routing row.
        services.AddSingleton<Core.Diagnostics.Ecam.IDiagnosticsCollector, Diagnostics.RemoteAudioDiagnosticsCollector>();

        return services;
    }
}
