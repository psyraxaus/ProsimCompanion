using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Checklists;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Checklists;

/// <summary>The FO-side control datarefs the sweep may write — the ONLY permitted targets
/// (captain-side refs are forbidden by both this list and the write gate).</summary>
public static class CopilotControls
{
    // The catalog descriptors carry the raw-neutral 512 as their fallback (#83) — a dead
    // axis must read centered, never deflected.
    public static readonly DataRef<int> Roll = ProsimDataRefNames.AnalogFoRoll;
    public static readonly DataRef<int> Pitch = ProsimDataRefNames.AnalogFoPitch;
    public static readonly DataRef<int> Rudder = ProsimDataRefNames.AnalogFoRudder;

    public const int Min = 0;
    public const int Max = 1024;

    public static bool IsWritable(string dataref)
        => TryResolve(dataref, out _);

    /// <summary>Resolves an AUTHORED (FO-side) axis dataref name from checklist JSON to its
    /// catalog descriptor; false for anything outside the three permitted axes.</summary>
    public static bool TryResolve(string dataref, out DataRef<int> axis)
    {
        if (dataref == Roll.Name)
        {
            axis = Roll;
            return true;
        }

        if (dataref == Pitch.Name)
        {
            axis = Pitch;
            return true;
        }

        if (dataref == Rudder.Name)
        {
            axis = Rudder;
            return true;
        }

        axis = default;
        return false;
    }

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
    private readonly IOptionsMonitor<SpeechOptions>? _speech;
    private readonly Dictionary<string, IDataRefSubscription<int>> _reads = new(StringComparer.Ordinal);

    public ControlSweepService(
        IProsimDataRefs dataRefs,
        ILogger<ControlSweepService> logger,
        IOptionsMonitor<SpeechOptions>? speech = null)
    {
        ArgumentNullException.ThrowIfNull(dataRefs);
        ArgumentNullException.ThrowIfNull(logger);

        _dataRefs = dataRefs;
        _logger = logger;
        _speech = speech;
    }

    /// <summary>Seat-relative target: checklist JSON is authored FO-side; with the human in
    /// the right seat the virtual pilot sweeps the captain-side analogs instead. The
    /// allow-list check stays on the AUTHORED name — the map only changes which side of an
    /// already-approved axis is written.</summary>
    private DataRef<int> Side(DataRef<int> axis)
        => PilotSeatMap.Map(axis,
            _speech is not null && PilotSeatMap.HumanIsRightSeat(_speech.CurrentValue));

    /// <summary>Runs the sweep to completion. Cancellation forces neutral, then rethrows.</summary>
    public async Task ExecuteAsync(ControlActionDefinition action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);

        try
        {
            foreach (var step in action.Steps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!CopilotControls.TryResolve(step.Dataref, out var axis))
                {
                    _logger.LogWarning("Sweep step refused — {Dataref} is not an FO control", step.Dataref);
                    continue;
                }

                await RampAsync(axis, step, cancellationToken).ConfigureAwait(false);
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
        foreach (var axis in new[] { CopilotControls.Pitch, CopilotControls.Roll, CopilotControls.Rudder })
        {
            try
            {
                // The descriptor fallback IS the raw neutral (#83).
                await _dataRefs.WriteAsync(Side(axis).Name, axis.Fallback).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Neutral write failed for {Dataref}", axis.Name);
            }
        }
    }

    private async Task RampAsync(DataRef<int> axis, ControlSweepStep step, CancellationToken cancellationToken)
    {
        var dataref = Side(axis);
        var target = CopilotControls.NormalizedToRaw(step.To);
        var current = ReadCurrent(dataref);

        if (step.RampMs <= 0 || current == target)
        {
            await _dataRefs.WriteAsync(dataref.Name, target, cancellationToken).ConfigureAwait(false);
            return;
        }

        var frames = Math.Max(1, step.RampMs * StepsPerSecond / 1000);
        var delay = Math.Max(1, step.RampMs / frames);
        for (var frame = 1; frame <= frames; frame++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = (int)Math.Round(current + (target - current) * (frame / (double)frames));
            await _dataRefs.WriteAsync(dataref.Name, value, cancellationToken).ConfigureAwait(false);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        // Land exactly on target regardless of rounding.
        await _dataRefs.WriteAsync(dataref.Name, target, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the axis's current raw value via a cached subscription (registered on
    /// first use); the descriptor's raw-neutral fallback when unknown.</summary>
    private int ReadCurrent(DataRef<int> dataref)
    {
        if (!_reads.TryGetValue(dataref.Name, out var subscription))
        {
            subscription = _dataRefs.Subscribe(dataref);
            _reads[dataref.Name] = subscription;
        }

        return subscription.Value;
    }
}
