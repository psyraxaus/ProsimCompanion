using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Checklists;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Abnormals;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Checklists;

/// <summary>
/// The voice checklist runner (Prosim2FO semantics): one cancellable async loop per checklist,
/// strictly sequential. Per item: speak the challenge, open a listening window (global
/// commands ∪ accepted phrases, digit grammar when a number is expected), wait indefinitely
/// (silence just keeps listening — only the sub-dialogues time out), match against
/// AcceptedPhrases (exact / whole-word / extracted number — ExpectedResponse is display-only),
/// then per behavior: acknowledge, verify-against-dataref with "are you sure" retries and the
/// never-give-up escape line, action (FO control sweep), monitorControls (captain sweep watch
/// then FO sweep). Runs beside the visual /checklists runner, which keeps its own gated
/// semantics — this engine is its own state machine, surfaced on the /speech page.
/// </summary>
public sealed class SpokenChecklistEngine : IDisposable
{
    private const int DefaultMaxRetries = 3;

    private enum ResponseKind
    {
        Phrase,
        Skip,
        SayAgain,
        NotCaught,
        Hold,
    }

    private sealed record EngineResponse(ResponseKind Kind, string Text);

    private readonly IOptionsMonitor<SpeechOptions> _options;
    private readonly ISpeechArbiter _arbiter;
    private readonly IRecognitionWindow _recognition;
    private readonly IMicOwnership _micOwnership;
    private readonly UtteranceInterpreter _interpreter;
    private readonly ChecklistService _checklists;
    private readonly IProsimDataRefs _dataRefs;
    private readonly ControlMonitor _monitor;
    private readonly ControlSweepService _sweep;
    private readonly FailureMonitor _failures;
    private readonly IReadOnlyList<IVoiceFeature> _features;
    private readonly SpeechStatusStore _store;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<SpokenChecklistEngine> _logger;
    private readonly PhraseBank _phrases = new();
    private readonly Briefings.MinimaCaptureDialogue _minimaCapture = null!;
    private readonly object _gate = new();
    private readonly Dictionary<string, IDataRefSubscription> _verifyReads = new(StringComparer.Ordinal);

    private CancellationTokenSource? _run;
    private TaskCompletionSource<EngineResponse>? _response;
    private ChecklistItemDefinition? _awaitingItem;
    private CancellationTokenSource? _monitorSkip;
    private bool _started;

    public SpokenChecklistEngine(
        IOptionsMonitor<SpeechOptions> options,
        ISpeechArbiter arbiter,
        IRecognitionWindow recognition,
        IMicOwnership micOwnership,
        UtteranceInterpreter interpreter,
        ChecklistService checklists,
        IProsimDataRefs dataRefs,
        ControlMonitor monitor,
        ControlSweepService sweep,
        FailureMonitor failures,
        IEnumerable<IVoiceFeature> features,
        SpeechStatusStore store,
        JsonlEventLog eventLog,
        ILogger<SpokenChecklistEngine> logger,
        Briefings.MinimaCaptureDialogue minimaCapture)
    {
        ArgumentNullException.ThrowIfNull(failures);
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(minimaCapture);
        _failures = failures;
        _features = [.. features];
        _minimaCapture = minimaCapture;
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(recognition);
        ArgumentNullException.ThrowIfNull(micOwnership);
        ArgumentNullException.ThrowIfNull(interpreter);
        ArgumentNullException.ThrowIfNull(checklists);
        ArgumentNullException.ThrowIfNull(dataRefs);
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(sweep);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _arbiter = arbiter;
        _recognition = recognition;
        _micOwnership = micOwnership;
        _interpreter = interpreter;
        _checklists = checklists;
        _dataRefs = dataRefs;
        _monitor = monitor;
        _sweep = sweep;
        _store = store;
        _eventLog = eventLog;
        _logger = logger;
    }

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _recognition.Accepted += OnRecognized;
        _recognition.Rejected += OnRejected;
        OpenIdleWindow();
    }

    public void Dispose()
    {
        _recognition.Accepted -= OnRecognized;
        _recognition.Rejected -= OnRejected;
        Cancel();
        foreach (var read in _verifyReads.Values)
        {
            read.Dispose();
        }
    }

    /// <summary>Starts a checklist by name (voice start-phrase or web button). Pinned to the
    /// default set: the web page's set selection must never change what the spoken run reads.</summary>
    public void StartChecklist(string name)
    {
        var definition = _checklists.Definitions(ChecklistService.DefaultSetName).FirstOrDefault(d =>
            d.Checklist.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (definition is null)
        {
            _ = Speak($"I don't have a {name} checklist.");
            return;
        }

        Cancel();
        var run = new CancellationTokenSource();
        lock (_gate)
        {
            _run = run;
        }

        _ = Task.Run(() => RunChecklistAsync(definition, run.Token));
    }

    /// <summary>Stops the active run; neutral-on-cancel returns the FO controls to center.</summary>
    public void Cancel()
    {
        CancellationTokenSource? run;
        lock (_gate)
        {
            run = _run;
            _run = null;
        }

        if (run is not null)
        {
            run.Cancel();
            _ = _sweep.ForceNeutralAsync();
        }
    }

    private async Task RunChecklistAsync(ChecklistDefinition definition, CancellationToken ct)
    {
        _store.Update(s => s with { SpokenChecklist = definition.Checklist, SpokenChecklistItem = "" });
        _eventLog.Record("checklist.voice", new { name = definition.Checklist, phase = "start" });

        // Drive the visual /checklists page alongside the spoken run: open the same checklist
        // there, then mark each line as the dialogue completes it (voice-freeze semantics, so
        // the visual runner's retreat pass can't un-check what the crew already read out).
        _checklists.Select(definition.Checklist);
        try
        {
            await Speak($"{definition.Checklist} checklist.").ConfigureAwait(false);

            for (var index = 0; index < definition.Items.Count; index++)
            {
                var item = definition.Items[index];
                ct.ThrowIfCancellationRequested();
                _store.Update(s => s with { SpokenChecklistItem = item.Say });

                var completed = item.Behavior.ToLowerInvariant() switch
                {
                    "verify" => await RunVerifyAsync(item, ct).ConfigureAwait(false),
                    "action" => await RunActionAsync(item, ct).ConfigureAwait(false),
                    "monitorcontrols" => await RunMonitorControlsAsync(item, ct).ConfigureAwait(false),
                    "captureminima" => await RunCaptureMinimaAsync(item, ct).ConfigureAwait(false),
                    _ => await RunAcknowledgeAsync(item, ct).ConfigureAwait(false),
                };
                if (completed)
                {
                    _checklists.VoiceComplete(definition.Checklist, index);
                }
                else
                {
                    _checklists.VoiceSkip(definition.Checklist, index);
                }
                _eventLog.Record("checklist.voice.item", new
                {
                    name = definition.Checklist,
                    item = item.Say,
                    result = completed ? "done" : "skipped",
                });
            }

            await Speak($"{definition.Checklist} checklist complete.").ConfigureAwait(false);
            _eventLog.Record("checklist.voice", new { name = definition.Checklist, phase = "end" });
        }
        catch (OperationCanceledException)
        {
            _eventLog.Record("checklist.voice", new { name = definition.Checklist, phase = "cancelled" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Voice checklist run failed");
        }
        finally
        {
            _store.Update(s => s with { SpokenChecklist = "", SpokenChecklistItem = "" });
            OpenIdleWindow();
        }
    }

    private async Task<bool> RunAcknowledgeAsync(ChecklistItemDefinition item, CancellationToken ct)
    {
        // An acknowledge/verify item that can never be answered (no accepted phrases, no
        // number) would loop "didn't catch that" forever — surface it instead of shipping it
        // silently (the predecessor validated this at load).
        if (item.AcceptedPhrases.Count == 0
            && !item.Expects.Equals("number", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Checklist item \"{Item}\" has no accepted phrases — auto-acknowledged", item.Say);
            await Speak(item.Say).ConfigureAwait(false);
            await SpeakConfirm(item).ConfigureAwait(false);
            return true;
        }

        await Speak(item.Say).ConfigureAwait(false);
        var answer = await AwaitAcceptedAsync(item, ct).ConfigureAwait(false);
        if (answer is null)
        {
            return false;
        }

        await SpeakConfirm(item).ConfigureAwait(false);
        return true;
    }

    /// <summary>The approach checklist's "Minimum" line: runs the interactive capture →
    /// read-back → confirm dialogue (Prosim2FO semantics — never proceed past minima on an
    /// unconfirmed value). A "not briefed" decision still completes the line; the answer IS
    /// the decision.</summary>
    private async Task<bool> RunCaptureMinimaAsync(ChecklistItemDefinition item, CancellationToken ct)
    {
        await Speak(item.Say).ConfigureAwait(false);
        var minima = await _minimaCapture.RunAsync(ct).ConfigureAwait(false);
        if (minima is null)
        {
            await Speak("Minimums not briefed.").ConfigureAwait(false);
        }

        await SpeakConfirm(item).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> RunVerifyAsync(ChecklistItemDefinition item, CancellationToken ct)
    {
        await Speak(item.Say).ConfigureAwait(false);
        var retries = 0;
        var max = item.MaxRetries ?? DefaultMaxRetries;
        while (true)
        {
            var answer = await AwaitAcceptedAsync(item, ct).ConfigureAwait(false);
            if (answer is null)
            {
                return false; // skipped
            }

            if (EvaluateResponse(item, answer))
            {
                await SpeakConfirm(item).ConfigureAwait(false);
                return true;
            }

            retries++;
            if (retries <= max)
            {
                await Speak(_phrases.NextAreYouSure()).ConfigureAwait(false);
            }
            else
            {
                // Never gives up — loops until set or skipped (predecessor semantics).
                retries = 0;
                await Speak($"{item.Say} still not set. Say \"skip\" to move on, or set it and respond again.")
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task<bool> RunActionAsync(ChecklistItemDefinition item, CancellationToken ct)
    {
        await Speak(item.Say).ConfigureAwait(false);
        var answer = await AwaitAcceptedAsync(item, ct).ConfigureAwait(false);
        if (answer is null)
        {
            return false;
        }

        if (item.Action is not null)
        {
            await _sweep.ExecuteAsync(item.Action, ct).ConfigureAwait(false);
        }

        await SpeakConfirm(item).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> RunMonitorControlsAsync(ChecklistItemDefinition item, CancellationToken ct)
    {
        await Speak(item.Say).ConfigureAwait(false);

        var composition = (item.Composition ?? "monitorThenSweep").ToLowerInvariant();
        if (composition != "sweeponly")
        {
            var outcome = await MonitorPhaseAsync(item, ct).ConfigureAwait(false);
            if (outcome == MonitorOutcome.Skipped)
            {
                await Speak("Flight controls check, skipped.").ConfigureAwait(false);
                return false;
            }

            if (outcome == MonitorOutcome.Aborted)
            {
                // ProSim dropped mid-check — saying "skipped" here would log a pilot decision
                // that never happened.
                await Speak("Flight controls check aborted — connection lost.").ConfigureAwait(false);
                return false;
            }
        }

        if (composition != "monitoronly" && item.Action is not null)
        {
            await _sweep.ExecuteAsync(item.Action, ct).ConfigureAwait(false);
        }

        await SpeakConfirm(item).ConfigureAwait(false);
        return true;
    }

    private enum MonitorOutcome
    {
        Completed,
        Skipped,

        /// <summary>ProSim dropped mid-check — not a pilot decision.</summary>
        Aborted,
    }

    /// <summary>Runs the captain-sweep watch with a command-only window open so skip/cancel
    /// still work.</summary>
    private async Task<MonitorOutcome> MonitorPhaseAsync(ChecklistItemDefinition item, CancellationToken ct)
    {
        var axes = new List<MonitorAxisSpec>();
        foreach (var (name, axis) in item.Axes ?? [])
        {
            var display = name.ToLowerInvariant() switch
            {
                "pitch" => "Elevator",
                "roll" => "Aileron",
                "rudder" => "Rudder",
                _ => name,
            };
            axes.Add(new MonitorAxisSpec(
                name, display, axis.Dataref, axis.FullPositive, axis.FullNegative, axis.Neutral));
        }

        if (axes.Count == 0)
        {
            return MonitorOutcome.Completed;
        }

        var spec = new MonitorSpec(
            axes,
            Sequenced: string.Equals(item.Mode, "sequenced", StringComparison.OrdinalIgnoreCase),
            item.DwellMs ?? 400,
            item.FullThreshold ?? 0.95,
            item.NeutralThreshold ?? 0.05);

        var skip = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_gate)
        {
            _monitorSkip = skip;
        }

        // Commands + feature phrases (reference semantics) — skip/cancel work and so does a
        // mid-check handover or radio call.
        var monitorGrammar = new List<string>(VoiceCommands.All);
        foreach (var feature in _features)
        {
            monitorGrammar.AddRange(feature.Phrases);
        }

        _recognition.OpenListeningWindow(monitorGrammar);
        try
        {
            var completed = await _monitor.RunAsync(
                spec, text => Speak(text), () => false, skip.Token).ConfigureAwait(false);
            return completed ? MonitorOutcome.Completed : MonitorOutcome.Aborted;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return MonitorOutcome.Skipped; // skip cancelled the monitor, not the run
        }
        finally
        {
            lock (_gate)
            {
                _monitorSkip = null;
            }

            skip.Dispose();
            _recognition.CloseListeningWindow();
        }
    }

    /// <summary>Opens the item's window and waits (indefinitely — silence keeps listening)
    /// for an accepted phrase; null means skipped.</summary>
    private async Task<string?> AwaitAcceptedAsync(ChecklistItemDefinition item, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var grammar = new List<string>(VoiceCommands.All);
            grammar.AddRange(item.AcceptedPhrases);
            if (item.Expects.Equals("number", StringComparison.OrdinalIgnoreCase))
            {
                grammar.Add(NumberGrammar.Sentinel);
            }

            var response = new TaskCompletionSource<EngineResponse>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _response = response;
                _awaitingItem = item;
            }

            _recognition.OpenListeningWindow(grammar);
            EngineResponse result;
            try
            {
                using (ct.Register(() => response.TrySetCanceled()))
                {
                    result = await response.Task.ConfigureAwait(false);
                }
            }
            finally
            {
                lock (_gate)
                {
                    _response = null;
                    _awaitingItem = null;
                }

                _recognition.CloseListeningWindow();
            }

            switch (result.Kind)
            {
                case ResponseKind.Skip:
                    await Speak($"Skipping {item.Say}.").ConfigureAwait(false);
                    return null;

                case ResponseKind.SayAgain:
                    await Speak(item.Say).ConfigureAwait(false);
                    continue;

                case ResponseKind.NotCaught:
                    await Speak(_phrases.NextDidNotCatch()).ConfigureAwait(false);
                    continue;

                case ResponseKind.Hold:
                    await HoldUntilResumedAsync(ct).ConfigureAwait(false);
                    await Speak(item.Say).ConfigureAwait(false); // re-challenge after the hold
                    continue;

                default:
                    if (IsAccepted(item, result.Text))
                    {
                        return result.Text;
                    }

                    await Speak(_phrases.NextDidNotCatch()).ConfigureAwait(false);
                    continue;
            }
        }
    }

    /// <summary>"hold the checklist" / "standby": acknowledge, then listen ONLY for the
    /// resume words (plus cancel/restart, which still route normally) so unrelated cockpit
    /// chatter can't accidentally resume. The item is re-challenged on resume.</summary>
    private async Task HoldUntilResumedAsync(CancellationToken ct)
    {
        await Speak("Holding the checklist. Say resume checklist when ready.").ConfigureAwait(false);
        _eventLog.Record("checklist.voice", new { phase = "hold" });
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var grammar = new List<string>(VoiceCommands.ResumeWords)
            {
                VoiceCommands.Cancel,
                VoiceCommands.Restart,
            };
            var response = new TaskCompletionSource<EngineResponse>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _response = response;
            }

            _recognition.OpenListeningWindow(grammar);
            EngineResponse result;
            try
            {
                using (ct.Register(() => response.TrySetCanceled()))
                {
                    result = await response.Task.ConfigureAwait(false);
                }
            }
            finally
            {
                lock (_gate)
                {
                    _response = null;
                }

                _recognition.CloseListeningWindow();
            }

            // Cancel/restart complete the run elsewhere; any resume word ends the hold. The
            // hold window routes non-answers here as Phrase responses.
            if (result.Kind is ResponseKind.Phrase
                && VoiceCommands.ResumeWords.Any(w => CommandMatcher.Normalize(result.Text)
                    .Equals(CommandMatcher.Normalize(w), StringComparison.Ordinal)))
            {
                _eventLog.Record("checklist.voice", new { phase = "resume" });
                await Speak("Resuming.").ConfigureAwait(false);
                return;
            }
        }
    }

    /// <summary>Matching is against AcceptedPhrases, never the display-only ExpectedResponse:
    /// extracted number (when expected), exact equality, or whole-word containment ("QNH 1017
    /// set" matches "set"; "reset" does not).</summary>
    private static bool IsAccepted(ChecklistItemDefinition item, string text)
    {
        if (item.Expects.Equals("number", StringComparison.OrdinalIgnoreCase)
            && NumberExtractor.TryExtract(text, out _))
        {
            return true;
        }

        var normalized = CommandMatcher.Normalize(text);
        foreach (var phrase in item.AcceptedPhrases)
        {
            var accepted = CommandMatcher.Normalize(phrase);
            if (accepted.Length == 0)
            {
                continue;
            }

            if (normalized.Equals(accepted, StringComparison.Ordinal))
            {
                return true;
            }

            if ($" {normalized} ".Contains($" {accepted} ", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The verify backstop: an ACCEPTED phrase whose dataref condition is false (or
    /// whose read-back number is out of tolerance) is what triggers "are you sure".</summary>
    private bool EvaluateResponse(ChecklistItemDefinition item, string answer)
    {
        if (item.Expects.Equals("number", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(item.ReadbackDataref)
            && NumberExtractor.TryExtract(answer, out var said))
        {
            var actual = ReadVerify(item.ReadbackDataref);
            var tolerance = item.ReadbackTolerance ?? 0.5;
            var ok = Math.Abs(said - actual) <= tolerance;
            _logger.LogInformation(
                "Readback {Ref}: said {Said}, actual {Actual}, tol {Tol} -> {Result}",
                item.ReadbackDataref, said, actual, tolerance, ok ? "pass" : "fail");
            return ok;
        }

        return item.Verify is null || ConditionEvaluator.Evaluate(item.Verify, ReadVerify);
    }

    private double ReadVerify(string dataref)
    {
        if (!_verifyReads.TryGetValue(dataref, out var read))
        {
            read = _dataRefs.Subscribe(dataref, DataRefTier.Normal);
            _verifyReads[dataref] = read;
        }

        return read.GetValue(0.0);
    }

    private void OnRecognized(object? sender, RecognizedEventArgs e)
    {
        // A borrowed mic means a guided dialogue (tech-log raise/rectify, shutdown offer) owns
        // recognition: normal routing — feature dispatch AND checklist answers — stands down.
        // A pending item's response source stays pending, so the checklist holds and resumes
        // when the borrow's disposal replays this engine's window.
        if (_micOwnership.IsBorrowed)
        {
            return;
        }

        try
        {
            RouteUtterance(e);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Utterance routing failed");
        }
    }

    private void OnRejected(object? sender, RecognizedEventArgs e)
    {
        if (_micOwnership.IsBorrowed)
        {
            return; // a dialogue owns the mic — its own listens handle rejection by timeout
        }

        // Empty-text rejections (silent PTT tap, timeout) are swallowed by design.
        if (!string.IsNullOrWhiteSpace(e.Text))
        {
            CompleteResponse(new EngineResponse(ResponseKind.NotCaught, ""));
        }
    }

    private void RouteUtterance(RecognizedEventArgs e)
    {
        ChecklistItemDefinition? awaiting;
        lock (_gate)
        {
            awaiting = _awaitingItem;
        }

        // Value-parsing features (FCU, radios) get the RAW transcription first so numbers
        // survive — skipped while an item is awaiting an answer.
        if (awaiting is null)
        {
            foreach (var feature in _features.Where(f => f.ValueParse))
            {
                if (feature.TryHandle(e.Text))
                {
                    return;
                }
            }
        }

        var grammar = BuildRouteVocabulary(awaiting);
        var interpretation = _interpreter.Interpret(
            e.Text, grammar, new InterpretContext(awaiting is not null, e.AcousticConfidence, e.NoSpeechProb));

        switch (interpretation.Kind)
        {
            case InterpretKind.Reject:
                if (!string.IsNullOrWhiteSpace(e.Text))
                {
                    CompleteResponse(new EngineResponse(ResponseKind.NotCaught, ""));
                }

                return;

            case InterpretKind.Confirm:
                // Gray band (score 0.70–0.85, command windows only): "did you mean …?" — the
                // predecessor's affirm-gated recovery instead of silently discarding it.
                _ = ConfirmAndRouteAsync(interpretation.Text);
                return;
        }

        RouteText(interpretation.Text, awaiting);
    }

    /// <summary>Post-interpretation routing (Prosim2FO's Route): an awaiting item's ACCEPTED
    /// answer outranks the identically-named global command; a non-answer falls through to
    /// the global commands and then the voice features, so "my aircraft" or "tune the ils"
    /// still works while a checklist line is pending.</summary>
    private void RouteText(string text, ChecklistItemDefinition? awaiting)
    {
        if (awaiting is not null && IsAccepted(awaiting, text))
        {
            CompleteResponse(new EngineResponse(ResponseKind.Phrase, text));
            return;
        }

        switch (CommandMatcher.Normalize(text))
        {
            case "skip" or "skip item":
                var monitorSkip = _monitorSkip;
                if (monitorSkip is not null)
                {
                    monitorSkip.Cancel();
                }
                else
                {
                    CompleteResponse(new EngineResponse(ResponseKind.Skip, ""));
                }

                return;

            case "say again" or "repeat":
                CompleteResponse(new EngineResponse(ResponseKind.SayAgain, ""));
                return;

            case "hold the checklist" or "standby":
                CompleteResponse(new EngineResponse(ResponseKind.Hold, ""));
                return;

            case "resume checklist" or "continue":
                // The hold loop owns the pending response while holding; outside a hold this
                // completes into an item answer that fails IsAccepted (didn't-catch) or
                // no-ops when nothing is pending.
                CompleteResponse(new EngineResponse(ResponseKind.Phrase, text));
                return;

            case "cancel checklist":
                Cancel();
                return;

            case "restart checklist":
                RestartActive();
                return;
        }

        // Voice features stay reachable while an item is pending (reference semantics) — a
        // handover or radio call must not become a failed checklist answer.
        foreach (var feature in _features)
        {
            if (feature.TryHandle(text))
            {
                return;
            }
        }

        if (awaiting is not null)
        {
            // Not a command, not a feature — treat as the item answer (IsAccepted already
            // failed above, so this lands in the "didn't catch that" flow).
            CompleteResponse(new EngineResponse(ResponseKind.Phrase, text));
            return;
        }

        // A memory-drill rehearsal phrase?
        if (_failures.TryRunDrillByPhrase(text))
        {
            return;
        }

        // A checklist start phrase? (Pinned to the default set — see StartChecklist.)
        foreach (var definition in _checklists.Definitions(ChecklistService.DefaultSetName))
        {
            var startPhrases = definition.StartPhrases is { Count: > 0 }
                ? definition.StartPhrases
                : [$"{definition.Checklist} checklist"];
            if (startPhrases.Any(p => CommandMatcher.Normalize(p)
                .Equals(CommandMatcher.Normalize(text), StringComparison.Ordinal)))
            {
                StartChecklist(definition.Checklist);
                return;
            }
        }
    }

    /// <summary>The gray-band recovery (Prosim2FO's ConfirmAndRouteAsync): borrow the mic,
    /// ask "did you mean {candidate}?", listen 8 s on the confirm vocabulary, and route the
    /// candidate only on an affirmative. Anything else (negative, timeout, mic busy) drops it
    /// — the borrow's disposal replays the previous window either way.</summary>
    private async Task ConfirmAndRouteAsync(string candidate)
    {
        try
        {
            IDisposable scope;
            try
            {
                scope = _micOwnership.Borrow("confirmCommand");
            }
            catch (InvalidOperationException)
            {
                return; // another dialogue owns the mic — let the pilot just repeat
            }

            string? answer;
            using (scope)
            {
                await Speak($"Say again — did you mean {candidate}?").ConfigureAwait(false);
                answer = await _micOwnership.ListenAsync(
                    ConfirmVocabulary.All, TimeSpan.FromSeconds(8), CancellationToken.None)
                    .ConfigureAwait(false);
            }

            if (answer is not null
                && ConfirmVocabulary.Affirm.Any(a => answer.Contains(a, StringComparison.OrdinalIgnoreCase)))
            {
                ChecklistItemDefinition? awaiting;
                lock (_gate)
                {
                    awaiting = _awaitingItem;
                }

                RouteText(candidate, awaiting);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Confirm dialogue failed for {Candidate}", candidate);
        }
    }

    private List<string> BuildRouteVocabulary(ChecklistItemDefinition? awaiting)
    {
        var vocabulary = new List<string>(VoiceCommands.All);
        if (awaiting is not null)
        {
            vocabulary.AddRange(awaiting.AcceptedPhrases);
            // Feature phrases stay in the item window (reference semantics): a handover or
            // radio call while a line is pending must snap and dispatch, not fail the item.
            foreach (var feature in _features)
            {
                vocabulary.AddRange(feature.Phrases);
            }
        }
        else
        {
            foreach (var definition in _checklists.Definitions(ChecklistService.DefaultSetName))
            {
                vocabulary.AddRange(definition.StartPhrases is { Count: > 0 }
                    ? definition.StartPhrases
                    : [$"{definition.Checklist} checklist"]);
            }

            vocabulary.AddRange(_failures.DrillPhrases);
            foreach (var feature in _features)
            {
                vocabulary.AddRange(feature.Phrases);
            }
        }

        return vocabulary;
    }

    private void RestartActive()
    {
        var name = _store.Snapshot().SpokenChecklist;
        if (!string.IsNullOrEmpty(name))
        {
            StartChecklist(name);
        }
    }

    private void CompleteResponse(EngineResponse response)
    {
        TaskCompletionSource<EngineResponse>? pending;
        lock (_gate)
        {
            pending = _response;
        }

        pending?.TrySetResult(response);
    }

    /// <summary>Idle grammar: every checklist's start phrases — always listening for a start.</summary>
    private void OpenIdleWindow()
        => _recognition.OpenListeningWindow(BuildRouteVocabulary(null));

    private async Task Speak(string text)
        => await _arbiter.EnqueueAsync(new SpeechRequest(text, SpeechPriority.Normal, Tag: "checklist"))
            .ConfigureAwait(false);

    private async Task SpeakConfirm(ChecklistItemDefinition item)
    {
        if (!string.IsNullOrWhiteSpace(item.ConfirmCallout))
        {
            await Speak(item.ConfirmCallout).ConfigureAwait(false);
        }
    }
}
