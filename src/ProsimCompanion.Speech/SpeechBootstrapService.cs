using Microsoft.Extensions.Hosting;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Callouts;
using ProsimCompanion.Speech.Monitoring;

namespace ProsimCompanion.Speech;

/// <summary>
/// Activates the speech pillar at startup (resolving the arbiter starts its pump; the callouts
/// engine and stabilized-approach monitor start their sampling timers here) and silences it
/// early at host shutdown — the arbiter's Dispose is idempotent, so the container disposing it
/// again afterwards is harmless.
/// </summary>
public sealed class SpeechBootstrapService : IHostedService
{
    private readonly SpeechArbiterService _arbiter;
    private readonly CalloutsEngine _callouts;
    private readonly StabilizedApproachMonitor _stabilized;
    private readonly FlowMonitor _flow;

    public SpeechBootstrapService(
        SpeechArbiterService arbiter,
        CalloutsEngine callouts,
        StabilizedApproachMonitor stabilized,
        FlowMonitor flow)
    {
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(callouts);
        ArgumentNullException.ThrowIfNull(stabilized);
        ArgumentNullException.ThrowIfNull(flow);

        _arbiter = arbiter;
        _callouts = callouts;
        _stabilized = stabilized;
        _flow = flow;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _callouts.Start();
        _stabilized.Start();
        _flow.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _callouts.Dispose();
        _stabilized.Dispose();
        _flow.Dispose();
        _arbiter.Dispose();
        return Task.CompletedTask;
    }
}
