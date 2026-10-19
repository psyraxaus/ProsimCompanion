using Microsoft.Extensions.Logging;
using ProsimCompanion.Gsx.Automation;

namespace ProsimCompanion.Gsx.Sync;

/// <summary>
/// Host around the pure <see cref="PushbackSequencer"/>: once per second the inputs gatherer
/// (<see cref="PushbackTickGatherer"/>) samples beacon, APU, doors and the GSX Pushback
/// service state, the pure planner (<see cref="PushbackTickEffectPlanner"/>) turns the hook
/// core's intents and the sequencer's action into an ordered effect list, and the applier
/// (<see cref="PushbackTickEffectApplier"/>) performs each through the door / jetway /
/// ground-equipment seam, decision-logging every transition. Also monitors the pushback
/// LVARs (<c>VEHICLE_PUSHBACK_STATE</c>, <c>PUSHBACK_STATUS</c>, <c>BYPASS_PIN</c>) so tug
/// progress is visible in the decision log, and answers the state-12 "confirm good engine
/// start" gate itself (issue #106 — the 2026-08-23 flight left the tug waiting until the
/// pilot opened the GSX menu by hand): once at least one engine is stably running with the
/// park brake set, the "Confirm good engine start" entry on GSX's "Interrupt pushback?" menu
/// is selected, Prosim2GSX-parity. What stays here: the timer, the re-entrancy guard, the
/// latch storage (<see cref="PushbackTickLatches"/>) and the sequencer's state.
/// </summary>
public sealed class GsxPushbackSequenceService : IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    private readonly PushbackTickGatherer _gatherer;
    private readonly PushbackTickEffectApplier _applier;
    private readonly PushbackTickLatches _latches;
    private readonly ILogger<GsxPushbackSequenceService> _logger;
    private readonly PushbackSequencer _sequencer;
    private readonly Timer _timer;
    private int _ticking;

    public GsxPushbackSequenceService(
        PushbackTickGatherer gatherer,
        PushbackTickEffectApplier applier,
        PushbackTickLatches latches,
        ILogger<GsxPushbackSequenceService> logger)
    {
        ArgumentNullException.ThrowIfNull(gatherer);
        ArgumentNullException.ThrowIfNull(applier);
        ArgumentNullException.ThrowIfNull(latches);
        ArgumentNullException.ThrowIfNull(logger);
        _gatherer = gatherer;
        _applier = applier;
        _latches = latches;
        _logger = logger;

        _sequencer = new PushbackSequencer(Random.Shared.Next);
        _timer = new Timer(_ => Tick(), null, TickInterval, TickInterval);
    }

    public void Dispose() => _timer.Dispose();

    /// <summary>One tick: gather → reset/tick the host-owned sequencer → plan (pure) → apply.
    /// Re-entrant-safe; a tick that overruns the next timer callback is skipped, not queued.</summary>
    private void Tick()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1)
        {
            return;
        }

        try
        {
            var reading = _gatherer.Read();
            var latches = _latches.Snapshot();

            // The sequencer is host-owned state: a new flight segment resets it before this
            // tick, and it only ticks with the sequence enabled and GSX ready.
            var sequenceWasReset = false;
            if (PushbackTickEffectPlanner.ResetDue(reading.Phase, latches.LastResetPhase)
                && _sequencer.Step != PushbackSequenceStep.Idle)
            {
                _sequencer.Reset();
                sequenceWasReset = true;
            }

            PushbackTickResult? tick = reading.SequenceRuns
                ? _sequencer.Tick(reading.SequenceInputs, reading.Delays)
                : null;

            foreach (var effect in PushbackTickEffectPlanner.Plan(reading, latches, sequenceWasReset, tick))
            {
                _applier.Apply(effect);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pushback sequence tick failed");
        }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }
}
