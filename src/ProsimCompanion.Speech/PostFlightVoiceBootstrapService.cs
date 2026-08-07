using Microsoft.Extensions.Hosting;
using ProsimCompanion.Speech.Debrief;
using ProsimCompanion.Speech.TechLog;

namespace ProsimCompanion.Speech;

/// <summary>
/// Activates the post-flight voice features (tech-log brief, spoken debrief) at startup.
/// Separate from <see cref="SpeechBootstrapService"/> so the whole pillar remains one
/// registration line and other speech features are untouched when it is absent.
/// </summary>
public sealed class PostFlightVoiceBootstrapService : IHostedService
{
    private readonly TechLogVoiceService _techLogVoice;
    private readonly DebriefService _debrief;

    public PostFlightVoiceBootstrapService(TechLogVoiceService techLogVoice, DebriefService debrief)
    {
        ArgumentNullException.ThrowIfNull(techLogVoice);
        ArgumentNullException.ThrowIfNull(debrief);

        _techLogVoice = techLogVoice;
        _debrief = debrief;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _techLogVoice.Start();
        _debrief.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _debrief.Dispose();
        _techLogVoice.Dispose();
        return Task.CompletedTask;
    }
}
