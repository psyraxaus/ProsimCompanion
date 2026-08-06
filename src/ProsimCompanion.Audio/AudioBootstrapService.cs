using Microsoft.Extensions.Hosting;

namespace ProsimCompanion.Audio;

/// <summary>
/// Activates the audio pillar's event wiring at startup (the injected singletons subscribe in
/// their constructors) and hands controlled volume targets back cleanly at host shutdown —
/// restored CoreAudio session volumes, VoiceMeeter targets at 0 dB, VBVMR logged out.
/// </summary>
public sealed class AudioBootstrapService : IHostedService
{
    private readonly AudioControlService _control;

    public AudioBootstrapService(AudioControlService control, ProsimNativeAudioGuard guard)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(guard); // injected purely to activate its ctor wiring

        _control = control;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _control.Shutdown();
        return Task.CompletedTask;
    }
}
