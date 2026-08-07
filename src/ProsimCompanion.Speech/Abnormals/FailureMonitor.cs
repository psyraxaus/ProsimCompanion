using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Checklists;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Arbiter;

namespace ProsimCompanion.Speech.Abnormals;

/// <summary>
/// ECAM abnormal detection + memory drills, detect-and-report only (Prosim2FO semantics):
/// E/WD text is the primary trigger, the per-system dataref condition corroborates or stands
/// in when the text dataref is empty, an optional master/ECAM light gates, a per-procedure
/// debounce filters transients, and a fired latch blocks re-announcement until the trigger
/// clears. Warnings and drills speak Critical (pre-empting); cautions High. A triggered drill
/// additionally speaks its rapid memory items (350 ms gaps) and closing status verbatim —
/// never persona-styled, never actuating anything.
/// </summary>
public sealed class FailureMonitor : IDisposable
{
    private const string EwdLeft = "aircraft.fwc.content.left.str";
    private const string MasterWarning = "system.indicators.I_MIP_MASTER_WARNING_FO";
    private const string MasterCaution = "system.indicators.I_MIP_MASTER_CAUTION_FO";
    private const int DrillGapMs = 350;

    private readonly ISpeechArbiter _arbiter;
    private readonly IProsimDataRefs _dataRefs;
    private readonly IFlightPhaseSource _flight;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<FailureMonitor> _logger;
    private readonly object _lock = new();
    private readonly Dictionary<string, IDataRefSubscription> _reads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TriggerState> _states = [];

    private IReadOnlyList<AbnormalDefinition> _definitions = [];
    private Timer? _timer;
    private int _ticking;
    private bool _ewdWarned;

    public FailureMonitor(
        ISpeechArbiter arbiter,
        IProsimDataRefs dataRefs,
        IFlightPhaseSource flight,
        JsonlEventLog eventLog,
        ILogger<FailureMonitor> logger)
    {
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(dataRefs);
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _arbiter = arbiter;
        _dataRefs = dataRefs;
        _flight = flight;
        _eventLog = eventLog;
        _logger = logger;
    }

    /// <summary>Drill voice phrases for the recognition idle grammar (drills are also
    /// voice-invocable as rehearsals).</summary>
    public IReadOnlyList<string> DrillPhrases
        => [.. _definitions.Where(d => d.IsDrill).SelectMany(d => d.VoiceTriggers)];

    public void Start()
    {
        Load(AbnormalLoader.LoadFolder(Path.Combine(AppContext.BaseDirectory, "config", "abnormals")));
        _logger.LogInformation("Loaded {Count} abnormal definitions ({Drills} drills)",
            _definitions.Count, _definitions.Count(d => d.IsDrill));
        _timer = new Timer(_ => Tick(), null, 1000, 500); // 2 Hz, fixed (the predecessor's detectionRateHz default; no config knob here yet)
    }

    /// <summary>Replaces the definition set — exposed for tests (Start loads from disk).</summary>
    public void Load(IReadOnlyList<AbnormalDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        lock (_lock)
        {
            _definitions = definitions;
            _states.Clear();
            foreach (var definition in _definitions)
            {
                _states[definition.Id] = new TriggerState();
            }
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        foreach (var read in _reads.Values)
        {
            read.Dispose();
        }
    }

    /// <summary>Runs a drill by voice phrase (ground rehearsal). Returns false when no drill
    /// matches.</summary>
    public bool TryRunDrillByPhrase(string phrase)
    {
        var drill = _definitions.FirstOrDefault(d => d.IsDrill
            && d.VoiceTriggers.Any(t => t.Equals(phrase, StringComparison.OrdinalIgnoreCase)));
        if (drill is null)
        {
            return false;
        }

        _eventLog.Record("drill.invoked", new { id = drill.Id, origin = "voice" });
        _ = SpeakDrillAsync(drill);
        return true;
    }

    /// <summary>One evaluation pass — public for tests.</summary>
    public void ProcessTick(DateTimeOffset nowUtc)
    {
        lock (_lock)
        {
            var phase = _flight.CurrentPhase;
            var ewdText = ReadEwdText();

            foreach (var definition in _definitions)
            {
                if (!definition.Enabled || !PhaseArmed(definition, phase))
                {
                    continue;
                }

                var state = _states[definition.Id];
                var signal = EvaluateTrigger(definition, ewdText);
                if (!signal)
                {
                    if (state.Fired)
                    {
                        state.Fired = false;
                        _eventLog.Record("failure.cleared", new { id = definition.Id });
                    }

                    state.RisingSinceUtc = null;
                    continue;
                }

                if (state.Fired)
                {
                    continue;
                }

                state.RisingSinceUtc ??= nowUtc;
                var debounce = TimeSpan.FromSeconds(definition.Trigger!.DebounceSeconds);
                if (nowUtc - state.RisingSinceUtc < debounce)
                {
                    continue;
                }

                state.Fired = true;
                Fire(definition);
            }
        }
    }

    private void Fire(AbnormalDefinition definition)
    {
        _eventLog.Record("failure.detected", new { id = definition.Id, title = definition.Title });
        if (definition.IsDrill)
        {
            _ = SpeakDrillAsync(definition, selfAnnounced: true);
            return;
        }

        var announcement = BuildAnnouncement(definition);
        var priority = definition.Severity.Equals("warning", StringComparison.OrdinalIgnoreCase)
            ? SpeechPriority.Critical
            : SpeechPriority.High;
        _ = _arbiter.EnqueueAsync(new SpeechRequest(
            announcement, priority, Tag: $"abnormal:{definition.Id}"));
    }

    private string BuildAnnouncement(AbnormalDefinition definition)
    {
        // Acknowledge the lit master light first (predecessor's acknowledgeMaster default).
        var warning = definition.Severity.Equals("warning", StringComparison.OrdinalIgnoreCase);
        var light = warning ? MasterWarning : MasterCaution;
        if (Read(light) > 0.5)
        {
            return (warning ? "Master warning. " : "Master caution. ") + definition.Announce;
        }

        return definition.Announce;
    }

    private async Task SpeakDrillAsync(AbnormalDefinition drill, bool selfAnnounced = false)
    {
        try
        {
            _eventLog.Record("drill.started", new { id = drill.Id });
            await _arbiter.EnqueueAsync(new SpeechRequest(
                drill.Announce, SpeechPriority.Critical, Tag: $"drill:{drill.Id}")).ConfigureAwait(false);

            foreach (var action in drill.Actions)
            {
                await Task.Delay(DrillGapMs).ConfigureAwait(false);
                await _arbiter.EnqueueAsync(new SpeechRequest(
                    action.Say, SpeechPriority.Critical, Tag: $"drill:{drill.Id}")).ConfigureAwait(false);
            }

            foreach (var status in drill.Status)
            {
                await Task.Delay(DrillGapMs).ConfigureAwait(false);
                await _arbiter.EnqueueAsync(new SpeechRequest(
                    status, SpeechPriority.Critical, Tag: $"drill:{drill.Id}")).ConfigureAwait(false);
            }

            _eventLog.Record("drill.completed", new { id = drill.Id });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Drill speech failed");
        }
    }

    private bool EvaluateTrigger(AbnormalDefinition definition, string ewdText)
    {
        var trigger = definition.Trigger!;
        bool? textHit = trigger.EwdText.Count > 0
            ? trigger.EwdText.Any(p => ewdText.Contains(p, StringComparison.OrdinalIgnoreCase))
            : null;

        bool? condHit = null;
        if (trigger.Condition is not null)
        {
            try
            {
                condHit = ConditionEvaluator.Evaluate(trigger.Condition, Read);
            }
            catch
            {
                condHit = false;
            }
        }

        bool signal;
        if (textHit is not null && condHit is not null)
        {
            signal = trigger.Logic.Equals("all", StringComparison.OrdinalIgnoreCase)
                ? textHit.Value && condHit.Value
                : textHit.Value || condHit.Value;
        }
        else
        {
            signal = textHit ?? condHit ?? false;
        }

        if (signal && !string.IsNullOrWhiteSpace(trigger.Corroborate) && Read(trigger.Corroborate) <= 0.5)
        {
            signal = false;
        }

        return signal;
    }

    private static bool PhaseArmed(AbnormalDefinition definition, FlightPhase phase)
        => definition.Phases.Count == 0
            || definition.Phases.Any(p => p.Equals(phase.ToString(), StringComparison.OrdinalIgnoreCase));

    private string ReadEwdText()
    {
        var text = Subscription(EwdLeft).GetValue("");
        if (text.Length == 0 && !_ewdWarned)
        {
            _ewdWarned = true;
            _logger.LogInformation(
                "E/WD text dataref returned no content — using dataref-condition triggers only");
        }

        return text;
    }

    private double Read(string dataref) => Subscription(dataref).GetValue(0.0);

    private IDataRefSubscription Subscription(string dataref)
    {
        if (!_reads.TryGetValue(dataref, out var read))
        {
            read = _dataRefs.Subscribe(dataref, DataRefTier.Normal);
            _reads[dataref] = read;
        }

        return read;
    }

    private void Tick()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1)
        {
            return;
        }

        try
        {
            ProcessTick(DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failure monitor tick failed");
        }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }

    private sealed class TriggerState
    {
        public bool Fired { get; set; }
        public DateTimeOffset? RisingSinceUtc { get; set; }
    }
}
