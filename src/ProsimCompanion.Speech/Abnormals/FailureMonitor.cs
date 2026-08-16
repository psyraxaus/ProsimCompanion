using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Checklists;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Abnormals;

/// <summary>
/// ECAM abnormal detection + memory drills, detect-and-report only (Prosim2FO semantics):
/// E/WD text is the primary trigger, the per-system dataref condition corroborates or stands
/// in when the text dataref is empty, an optional master/ECAM light gates, a per-procedure
/// debounce filters transients, and a fired latch blocks re-announcement until the trigger
/// clears. Warnings and drills speak Critical (pre-empting); cautions High. A triggered drill
/// additionally speaks its rapid memory items (350 ms gaps) and closing status verbatim —
/// never persona-styled, never actuating anything. A triggered ECAM procedure with action
/// lines follows its announcement with the interactive per-line dialogue
/// (<see cref="EcamDialogueCore"/>) under an exclusive mic borrow — one dialogue at a time,
/// FIFO, skipped if the failure clears before its turn, and disabled entirely (announce-only)
/// when no <see cref="IMicOwnership"/> was supplied.
/// </summary>
public sealed class FailureMonitor : IEcamDialogueIo, Core.State.IAbnormalDialogueControl, IDisposable
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
    private readonly IMicOwnership? _mic;
    private readonly Core.State.SpeechStatusStore? _status;
    private readonly Core.State.ConfigProblemStore? _configProblems;
    private readonly EcamDialogueCore _ecamDialogue;
    private readonly SemaphoreSlim _oneDialogue = new(1, 1);
    private readonly CancellationTokenSource _dialogueCts = new();
    private readonly object _lock = new();
    private readonly Dictionary<string, IDataRefSubscription> _reads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TriggerState> _states = [];

    private IReadOnlyList<AbnormalDefinition> _definitions = [];
    private DialogueSession? _activeDialogue;
    private Timer? _timer;
    private int _ticking;
    private bool _ewdWarned;

    /// <summary>Optional <paramref name="micOwnership"/>: DI injects the registered seam, so
    /// live ECAM procedures run the interactive dialogue; without it (degraded mode, and the
    /// pre-existing detection tests) procedures are announce-only and drills are unaffected.
    /// Optional <paramref name="statusStore"/> publishes the running dialogue's title for the
    /// web page's cancel affordance (issue #56). Optional <paramref name="configProblems"/>
    /// surfaces malformed abnormal files on the web UI (issue #74).</summary>
    public FailureMonitor(
        ISpeechArbiter arbiter,
        IProsimDataRefs dataRefs,
        IFlightPhaseSource flight,
        JsonlEventLog eventLog,
        ILogger<FailureMonitor> logger,
        IMicOwnership? micOwnership = null,
        Core.State.SpeechStatusStore? statusStore = null,
        Core.State.ConfigProblemStore? configProblems = null)
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
        _mic = micOwnership;
        _status = statusStore;
        _configProblems = configProblems;
        _ecamDialogue = new EcamDialogueCore(this, eventLog);
    }

    /// <summary>The running ECAM dialogue and the means to end it early. Voice abort lives in
    /// the dialogue core; this cancellation path serves the web button (user) and
    /// <see cref="ProcessTick"/> (the failure cleared mid-dialogue, issue #57).</summary>
    private sealed class DialogueSession
    {
        public required string Id { get; init; }

        public required string Title { get; init; }

        public required CancellationTokenSource Cts { get; init; }

        /// <summary>Why the session was cancelled ("cleared" / "user") — set BEFORE
        /// <see cref="Cts"/> fires so the unwind path can announce the right thing.</summary>
        public string? EndReason { get; set; }
    }

    /// <summary>Drill voice phrases for the recognition idle grammar (drills are also
    /// voice-invocable as rehearsals).</summary>
    public IReadOnlyList<string> DrillPhrases
        => [.. _definitions.Where(d => d.IsDrill).SelectMany(d => d.VoiceTriggers)];

    /// <summary>Raised (id, title) when a real abnormal fires — the seam the tech log uses to
    /// remember which abnormals happened this flight (for the deferred post-abnormal offer)
    /// without re-parsing the event log. Fires on the monitor's tick thread.</summary>
    public event Action<string, string>? FailureDetected;

    public void Start()
    {
        // User tree, not the install dir (ADR-0007) — seeded from shipped defaults at startup.
        // Malformed files log AND surface on the web banner (issue #74) instead of the old
        // silent skip.
        var failed = 0;
        _configProblems?.ClearArea(Core.State.ConfigAreas.Abnormals);
        Load(AbnormalLoader.LoadFolder(Core.Configuration.UserConfigPaths.Abnormals, (file, message) =>
        {
            failed++;
            _logger.LogWarning("Abnormal file {File} failed to load: {Message}", file, message);
            _configProblems?.Report(Core.State.ConfigAreas.Abnormals, file, message);
        }));
        _logger.LogInformation("Loaded {Count} abnormal definitions ({Drills} drills); {Failed} file(s) failed to parse",
            _definitions.Count, _definitions.Count(d => d.IsDrill), failed);
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
        _dialogueCts.Cancel(); // stops a running/queued ECAM dialogue mid-line
        _timer?.Dispose();
        foreach (var read in _reads.Values)
        {
            read.Dispose();
        }

        _dialogueCts.Dispose();
        _oneDialogue.Dispose();
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
        // Cancelled outside the lock: Cancel() can run continuations synchronously, and one
        // of those may re-enter this monitor.
        CancellationTokenSource? clearedDialogue = null;

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

                        // A dialogue mid-run for this failure ends now (issue #57): the
                        // 2026-08-15 flight had the FO demanding a gen reset for 13 minutes
                        // after the generators came back on line.
                        if (_activeDialogue is { } active
                            && string.Equals(active.Id, definition.Id, StringComparison.Ordinal))
                        {
                            active.EndReason = "cleared";
                            clearedDialogue = active.Cts;
                        }
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

        clearedDialogue?.Cancel();
    }

    /// <summary>Web-button escape (issue #56): ends the running ECAM dialogue without needing
    /// a recognized voice command. The unwind path acknowledges aloud and returns the mic.</summary>
    public bool CancelActiveDialogue()
    {
        CancellationTokenSource? cts;
        lock (_lock)
        {
            if (_activeDialogue is not { } active)
            {
                return false;
            }

            active.EndReason = "user";
            cts = active.Cts;
        }

        cts.Cancel();
        return true;
    }

    private void Fire(AbnormalDefinition definition)
    {
        _eventLog.Record("failure.detected", new { id = definition.Id, title = definition.Title });
        try
        {
            FailureDetected?.Invoke(definition.Id, definition.Title);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "FailureDetected subscriber threw for {Id}", definition.Id);
        }
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

        // The announcement always plays; the interactive per-line dialogue follows only when
        // there are action lines to work and a mic seam to hold them on.
        if (_mic is not null && definition.Actions.Count > 0)
        {
            _ = Task.Run(() => RunEcamDialogueAsync(definition));
        }
    }

    /// <summary>
    /// One ECAM dialogue at a time (FIFO by semaphore turn — the severity pre-emption of
    /// Prosim2FO's engine is not ported; a later Critical announcement still jumps the audio
    /// queue). When its turn comes, a detection that has already cleared is skipped, matching
    /// the predecessor. A RUNNING dialogue additionally carries its own cancellation source so
    /// the web button (issue #56) and a failure clearing mid-run (issue #57) can end it — the
    /// unwind announces why. The mic borrow's using-disposal restores normal routing on every
    /// path.
    /// </summary>
    private async Task RunEcamDialogueAsync(AbnormalDefinition definition)
    {
        var token = _dialogueCts.Token;
        try
        {
            await _oneDialogue.WaitAsync(token).ConfigureAwait(false);
            DialogueSession? session = null;
            try
            {
                lock (_lock)
                {
                    if (_states.TryGetValue(definition.Id, out var state) && !state.Fired)
                    {
                        _eventLog.Record("abnormal.skipped",
                            new { id = definition.Id, reason = "cleared-before-run" });
                        _logger.LogInformation(
                            "Skipping ECAM dialogue for {Id} — already cleared", definition.Id);
                        return;
                    }

                    session = new DialogueSession
                    {
                        Id = definition.Id,
                        Title = definition.Title,
                        Cts = CancellationTokenSource.CreateLinkedTokenSource(token),
                    };
                    _activeDialogue = session;
                }

                _status?.Update(s => s with { ActiveAbnormal = definition.Title });

                try
                {
                    using (await BorrowMicAsync("abnormal:" + definition.Id, session.Cts.Token).ConfigureAwait(false))
                    {
                        await _ecamDialogue.RunAsync(definition, session.Cts.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    // Session-only cancellation: the fault cleared, or the user hit the web
                    // cancel. Announce on a fresh token — the session's is already cancelled.
                    var reason = session.EndReason ?? "user";
                    _eventLog.Record("abnormal.ended", new { id = definition.Id, reason });
                    var announcement = reason == "cleared"
                        ? $"The {definition.Title} has cleared. Resuming normal duties."
                        : "ECAM cancelled. Resuming normal duties.";
                    await ((IEcamDialogueIo)this)
                        .SpeakAsync(announcement, CancellationToken.None).ConfigureAwait(false);
                }
            }
            finally
            {
                lock (_lock)
                {
                    if (ReferenceEquals(_activeDialogue, session))
                    {
                        _activeDialogue = null;
                    }
                }

                _status?.Update(s => s with { ActiveAbnormal = "" });
                session?.Cts.Dispose();
                try
                {
                    _oneDialogue.Release();
                }
                catch (ObjectDisposedException)
                {
                    // Disposed while this dialogue was finishing — nothing left to release to.
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down — the dialogue just stops.
        }
        catch (ObjectDisposedException)
        {
            // Disposed while waiting our turn — same as cancellation.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ECAM dialogue failed for {Id}", definition.Id);
        }
    }

    /// <summary>Waits for the mic if another guided dialogue (tech log, minima capture) holds
    /// it — an abnormal is urgent but Borrow throws on overlap by design, so we poll rather
    /// than crash either dialogue.</summary>
    private async Task<IDisposable> BorrowMicAsync(string owner, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_mic!.IsBorrowed)
            {
                try
                {
                    return _mic.Borrow(owner);
                }
                catch (InvalidOperationException)
                {
                    // Lost the race to another dialogue — keep waiting.
                }
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
    }

    // ---- IEcamDialogueIo (the dialogue core's speak/listen/verify seam) ----

    Task IEcamDialogueIo.SpeakAsync(string text, CancellationToken cancellationToken)
        => string.IsNullOrWhiteSpace(text)
            ? Task.CompletedTask
            : _arbiter.EnqueueAsync(
                new SpeechRequest(text, SpeechPriority.High, Tag: "abnormal"), cancellationToken);

    Task<string?> IEcamDialogueIo.ListenAsync(
        IReadOnlyList<string> grammar, TimeSpan timeout, CancellationToken cancellationToken)
        => _mic!.ListenAsync(grammar, timeout, cancellationToken);

    /// <summary>Null (→ ask the pilot) when any leaf dataref has no pushed value yet or went
    /// stale on a connection drop — the analogue of Prosim2FO's "not connected → Ask", since
    /// <see cref="ConditionEvaluator"/> itself is fail-closed and never throws.</summary>
    bool? IEcamDialogueIo.TryEvaluate(VerifyCondition condition)
    {
        ArgumentNullException.ThrowIfNull(condition);

        lock (_lock)
        {
            return AnyLeafUnreadable(condition) ? null : ConditionEvaluator.Evaluate(condition, Read);
        }
    }

    private bool AnyLeafUnreadable(VerifyCondition condition)
    {
        if (condition.IsCompound)
        {
            return condition.Conditions!.Any(AnyLeafUnreadable);
        }

        if (string.IsNullOrWhiteSpace(condition.Dataref))
        {
            return false; // malformed leaf — let the evaluator fail it closed
        }

        var subscription = Subscription(condition.Dataref);
        return subscription.RawValue is null || subscription.IsStale;
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
