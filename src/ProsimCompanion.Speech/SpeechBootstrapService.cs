using Microsoft.Extensions.Hosting;
using ProsimCompanion.Speech.Arbiter;

namespace ProsimCompanion.Speech;

/// <summary>
/// Activates the speech pillar at startup (resolving the arbiter starts its pump) and
/// silences it early at host shutdown — the arbiter's Dispose is idempotent, so the container
/// disposing it again afterwards is harmless.
/// </summary>
public sealed class SpeechBootstrapService : IHostedService
{
    private readonly SpeechArbiterService _arbiter;

    public SpeechBootstrapService(SpeechArbiterService arbiter)
    {
        ArgumentNullException.ThrowIfNull(arbiter);
        _arbiter = arbiter;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _arbiter.Dispose();
        return Task.CompletedTask;
    }
}
