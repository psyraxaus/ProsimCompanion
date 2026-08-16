using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Checklists;

namespace ProsimCompanion.Speech.Checklists;

/// <summary>One monitored axis resolved for a run.</summary>
public sealed record MonitorAxisSpec(
    string Name,
    string Display,
    string Dataref,
    string FullPositiveCallout,
    string FullNegativeCallout,
    string NeutralCallout);

/// <summary>Monitor configuration resolved from the checklist item (defaults applied).</summary>
public sealed record MonitorSpec(
    IReadOnlyList<MonitorAxisSpec> Axes,
    bool Sequenced,
    int DwellMs,
    double FullThreshold,
    double NeutralThreshold);

/// <summary>
/// Watches the captain's flight-control sweep (read-only) and calls out each settled position:
/// ~30 Hz sampling, a zone must persist the dwell time before it is called (anti-bounce), an
/// axis completes after full+/full− both seen and settled back to neutral. Reactive mode
/// accepts any order; sequenced announces each axis and waits for it. A 15 s idle stall
/// reprompts once per stall. Semantics carried from Prosim2FO.
/// </summary>
public sealed class ControlMonitor
{
    private const int SampleIntervalMs = 33;
    private const int IdleRepromptMs = 15_000;

    private enum Zone
    {
        Between,
        Neutral,
        FullPositive,
        FullNegative,
    }

    private readonly IProsimDataRefs _dataRefs;
    private readonly ILogger<ControlMonitor> _logger;

    public ControlMonitor(IProsimDataRefs dataRefs, ILogger<ControlMonitor> logger)
    {
        ArgumentNullException.ThrowIfNull(dataRefs);
        ArgumentNullException.ThrowIfNull(logger);

        _dataRefs = dataRefs;
        _logger = logger;
    }

    /// <summary>Runs until every axis completes (true) or the token cancels (skip/cancel —
    /// OperationCanceledException propagates to the caller's skip handling).</summary>
    public async Task<bool> RunAsync(
        MonitorSpec spec,
        Func<string, Task> speakAsync,
        Func<bool> isPaused,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(speakAsync);
        ArgumentNullException.ThrowIfNull(isPaused);

        // Escape hatch (#83): axis dataref names come from the user-editable checklist JSON
        // (seat-mapped by the engine) — they only exist at runtime. Critical tier is
        // deliberate: the 33 ms sampling loop below needs the 100 ms push cadence to catch a
        // brisk sweep's full stops.
        var subscriptions = spec.Axes
            .ToDictionary(a => a.Name, a => _dataRefs.SubscribeDynamic(a.Dataref, DataRefTier.Critical));
        try
        {
            var state = spec.Axes.ToDictionary(a => a.Name, _ => new AxisState());
            var sequenceIndex = 0;
            var lastActivity = Environment.TickCount64;
            var reprompted = false;

            if (spec.Sequenced && spec.Axes.Count > 0)
            {
                await speakAsync(spec.Axes[0].Display).ConfigureAwait(false);
            }

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                while (isPaused())
                {
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                }

                // ProSim gone: abort silently — the checklist run surfaces the situation.
                if (subscriptions.Values.Any(s => s.IsStale))
                {
                    _logger.LogInformation("Control monitor aborted — ProSim disconnected");
                    return false;
                }

                var activeAxes = spec.Sequenced
                    ? [spec.Axes[sequenceIndex]]
                    : spec.Axes.Where(a => !state[a.Name].IsComplete).ToList();

                foreach (var axis in activeAxes)
                {
                    var axisState = state[axis.Name];
                    var value = subscriptions[axis.Name].GetValue(0.0);
                    var zone = Classify(value, spec.FullThreshold, spec.NeutralThreshold);

                    if (zone != axisState.PendingZone)
                    {
                        axisState.PendingZone = zone;
                        axisState.PendingSinceMs = Environment.TickCount64;
                        continue;
                    }

                    if (zone == axisState.SettledZone
                        || Environment.TickCount64 - axisState.PendingSinceMs < spec.DwellMs)
                    {
                        continue;
                    }

                    axisState.SettledZone = zone;
                    lastActivity = Environment.TickCount64;
                    reprompted = false;

                    // Callouts are fire-and-forget: awaiting to playback-complete would stall
                    // sampling for the utterance duration, and a brisk captain sweep can pass
                    // a full stop in well under "Full up" airtime. The arbiter's FIFO keeps
                    // the callouts in order regardless.
                    switch (zone)
                    {
                        case Zone.FullPositive when !axisState.HasPositive:
                            axisState.HasPositive = true;
                            _ = speakAsync(axis.FullPositiveCallout);
                            break;

                        case Zone.FullNegative when !axisState.HasNegative:
                            axisState.HasNegative = true;
                            _ = speakAsync(axis.FullNegativeCallout);
                            break;

                        case Zone.Neutral when axisState.HasPositive && axisState.HasNegative
                            && !axisState.IsComplete:
                            axisState.IsComplete = true;
                            _ = speakAsync(axis.NeutralCallout);
                            break;
                    }
                }

                if (spec.Sequenced)
                {
                    if (state[spec.Axes[sequenceIndex].Name].IsComplete)
                    {
                        sequenceIndex++;
                        if (sequenceIndex >= spec.Axes.Count)
                        {
                            return true;
                        }

                        _ = speakAsync(spec.Axes[sequenceIndex].Display);
                    }
                }
                else if (state.Values.All(s => s.IsComplete))
                {
                    return true;
                }

                if (!reprompted && Environment.TickCount64 - lastActivity > IdleRepromptMs)
                {
                    reprompted = true;
                    _ = speakAsync("Flight controls — move the controls to the stops, or say skip.");
                }

                await Task.Delay(SampleIntervalMs, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            foreach (var subscription in subscriptions.Values)
            {
                subscription.Dispose();
            }
        }
    }

    private static Zone Classify(double value, double full, double neutral) => value switch
    {
        _ when value >= full => Zone.FullPositive,
        _ when value <= -full => Zone.FullNegative,
        _ when Math.Abs(value) <= neutral => Zone.Neutral,
        _ => Zone.Between,
    };

    private sealed class AxisState
    {
        public Zone PendingZone { get; set; } = Zone.Between;
        public long PendingSinceMs { get; set; }
        public Zone SettledZone { get; set; } = Zone.Between;
        public bool HasPositive { get; set; }
        public bool HasNegative { get; set; }
        public bool IsComplete { get; set; }
    }
}
