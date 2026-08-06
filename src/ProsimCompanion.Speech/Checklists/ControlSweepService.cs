using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Checklists;

namespace ProsimCompanion.Speech.Checklists;

/// <summary>The FO-side control datarefs the sweep may write — the ONLY permitted targets
/// (captain-side refs are forbidden by both this list and the write gate).</summary>
public static class CopilotControls
{
    public const string Roll = "system.analog.A_FC_FO_ROLL";
    public const string Pitch = "system.analog.A_FC_FO_PITCH";
    public const string Rudder = "system.analog.A_FC_FO_RUDDER";

    public const int Min = 0;
    public const int NeutralRaw = 512;
    public const int Max = 1024;

    public static bool IsWritable(string dataref)
        => dataref is Roll or Pitch or Rudder;

    /// <summary>Normalized −1..+1 → raw 0..1024 (512 = neutral).</summary>
    public static int NormalizedToRaw(double normalized)
        => Math.Clamp((int)Math.Round(Math.Clamp(normalized, -1, 1) * 512 + 512), Min, Max);
}

/// <summary>
/// Executes the FO's scripted flight-control sweep: linear ramps at 50 steps/s from the
/// current raw value to each target, with holds. Safety semantics carried from Prosim2FO: an
/// aborted sweep (cancel/disconnect) forces all three axes back to neutral before rethrowing;
/// ForceNeutral is also the cancel/shutdown hook. Non-allow-listed datarefs are refused here
/// AND by the write gate.
/// </summary>
public sealed class ControlSweepService
{
    private const int StepsPerSecond = 50;

    private readonly IProsimDataRefs _dataRefs;
    private readonly ILogger<ControlSweepService> _logger;
    private readonly Dictionary<string, IDataRefSubscription> _reads = new(StringComparer.Ordinal);

    public ControlSweepService(IProsimDataRefs dataRefs, ILogger<ControlSweepService> logger)
    {
        ArgumentNullException.ThrowIfNull(dataRefs);
        ArgumentNullException.ThrowIfNull(logger);

        _dataRefs = dataRefs;
        _logger = logger;
    }

    /// <summary>Runs the sweep to completion. Cancellation forces neutral, then rethrows.</summary>
    public async Task ExecuteAsync(ControlActionDefinition action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);

        try
        {
            foreach (var step in action.Steps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!CopilotControls.IsWritable(step.Dataref))
                {
                    _logger.LogWarning("Sweep step refused — {Dataref} is not an FO control", step.Dataref);
                    continue;
                }

                await RampAsync(step, cancellationToken).ConfigureAwait(false);
                if (step.HoldMs > 0)
                {
                    await Task.Delay(step.HoldMs, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Control sweep aborted — forcing neutral");
            await ForceNeutralAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Returns all three FO axes to neutral (512). Never throws — this is the
    /// safety path; failures are logged and swallowed.</summary>
    public async Task ForceNeutralAsync()
    {
        foreach (var dataref in new[] { CopilotControls.Pitch, CopilotControls.Roll, CopilotControls.Rudder })
        {
            try
            {
                await _dataRefs.WriteAsync(dataref, CopilotControls.NeutralRaw).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Neutral write failed for {Dataref}", dataref);
            }
        }
    }

    private async Task RampAsync(ControlSweepStep step, CancellationToken cancellationToken)
    {
        var target = CopilotControls.NormalizedToRaw(step.To);
        var current = ReadCurrent(step.Dataref);

        if (step.RampMs <= 0 || current == target)
        {
            await _dataRefs.WriteAsync(step.Dataref, target, cancellationToken).ConfigureAwait(false);
            return;
        }

        var frames = Math.Max(1, step.RampMs * StepsPerSecond / 1000);
        var delay = Math.Max(1, step.RampMs / frames);
        for (var frame = 1; frame <= frames; frame++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = (int)Math.Round(current + (target - current) * (frame / (double)frames));
            await _dataRefs.WriteAsync(step.Dataref, value, cancellationToken).ConfigureAwait(false);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        // Land exactly on target regardless of rounding.
        await _dataRefs.WriteAsync(step.Dataref, target, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the axis's current raw value via a cached subscription (registered on
    /// first use); 512 when unknown.</summary>
    private int ReadCurrent(string dataref)
    {
        if (!_reads.TryGetValue(dataref, out var subscription))
        {
            subscription = _dataRefs.Subscribe(dataref, DataRefTier.Normal);
            _reads[dataref] = subscription;
        }

        return subscription.GetValue(CopilotControls.NeutralRaw);
    }
}
