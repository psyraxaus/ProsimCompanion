using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Speech.Llm;

/// <summary>
/// Background LLM health probe (issue #66). The diagnostics page only ever tested the LLM
/// on demand, so a 401 discovered at startup stayed invisible for the whole flight AND a
/// later recovery (key fixed, host woken) went unnoticed. This service probes once shortly
/// after startup to seed <see cref="LlmHealthStore"/>, then re-probes every five minutes
/// while the state is unhealthy so recovery is detected. While Healthy it stays quiet —
/// real calls keep the store current. Degrade-not-fail: an unconfigured LLM never probes
/// and the loop never throws out of the host.
/// </summary>
public sealed class LlmHealthProbeService : IHostedService, IDisposable
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ReprobeInterval = TimeSpan.FromMinutes(5);

    private readonly OpenAiChatClient _llm;
    private readonly LlmHealthStore _health;
    private readonly ILogger<LlmHealthProbeService> _logger;
    private readonly CancellationTokenSource _stop = new();

    private Task? _loop;

    public LlmHealthProbeService(
        OpenAiChatClient llm,
        LlmHealthStore health,
        ILogger<LlmHealthProbeService> logger)
    {
        ArgumentNullException.ThrowIfNull(llm);
        ArgumentNullException.ThrowIfNull(health);
        ArgumentNullException.ThrowIfNull(logger);

        _llm = llm;
        _health = health;
        _logger = logger;
    }

    /// <summary>The probe decision, extracted pure for tests: probe when configured AND the
    /// state is not yet known (startup seed) or known-unhealthy (recovery watch). Healthy
    /// needs no probing — live calls report their own outcomes.</summary>
    public static bool ShouldProbe(bool configured, LlmHealthState state)
        => configured && state is LlmHealthState.Unknown
            or LlmHealthState.AuthFailed
            or LlmHealthState.Unreachable;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _loop = Task.Run(() => RunAsync(_stop.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stop.Cancel();
        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // A probe mid-flight at shutdown — abandoned, nothing to clean up.
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    public void Dispose() => _stop.Dispose();

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(InitialDelay, ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                if (ShouldProbe(_llm.IsConfigured, _health.Snapshot().State))
                {
                    await ProbeAsync(ct).ConfigureAwait(false);
                }

                await Task.Delay(ReprobeInterval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LLM health probe loop stopped unexpectedly");
        }
    }

    private async Task ProbeAsync(CancellationToken ct)
    {
        var before = _health.Snapshot().State;
        try
        {
            // CompleteAsync reports the outcome to the health store itself — the probe only
            // has to make the call and log the transition.
            _ = await _llm.CompleteAsync(
                "You are a connectivity test. Reply with the single word OK.", "ping", ct)
                .ConfigureAwait(false);
            if (before is not LlmHealthState.Healthy)
            {
                _logger.LogInformation("LLM endpoint recovered — free-form styling is back online");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var state = _health.Snapshot();
            if (before != state.State)
            {
                // First detection of this failure mode gets a Warning; repeats stay at Debug
                // so the five-minute cadence never floods the log.
                _logger.LogWarning(
                    "LLM endpoint unhealthy ({State}): {Summary}",
                    state.State, state.LastError ?? ex.Message);
            }
            else
            {
                _logger.LogDebug(ex, "LLM health re-probe still failing ({State})", state.State);
            }
        }
    }
}
