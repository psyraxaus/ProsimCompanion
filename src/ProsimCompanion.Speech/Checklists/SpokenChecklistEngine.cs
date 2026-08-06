using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Checklists;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.State;
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
    }

    private sealed record EngineResponse(ResponseKind Kind, string Text);

    private readonly IOptionsMonitor<SpeechOptions> _options;
    private readonly ISpeechArbiter _arbiter;
    private readonly RecognitionController _recognition;
    private readonly UtteranceInterpreter _interpreter;
    private readonly ChecklistService _checklists;
    private readonly IProsimDataRefs _dataRefs;
    private readonly ControlMonitor _monitor;
    private readonly ControlSweepService _sweep;
    private readonly SpeechStatusStore _store;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<SpokenChecklistEngine> _logger;
    private readonly PhraseBank _phrases = new();
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
        RecognitionController recognition,
        UtteranceInterpreter interpreter,
        ChecklistService checklists,
        IProsimDataRefs dataRefs,
        ControlMonitor monitor,
        ControlSweepService sweep,
        SpeechStatusStore store,
        JsonlEventLog eventLog,
        ILogger<SpokenChecklistEngine> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(recognition);
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

    /// <summary>Starts a checklist by name (voice start-phrase or web button).</summary>
    public void StartChecklist(string name)
    {
        var definition = _checklists.Definitions().FirstOrDefault(d =>
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
        try
        {
            await Speak($"{definition.Checklist} checklist.").ConfigureAwait(false);

            foreach (var item in definition.Items)
            {
                ct.ThrowIfCancellationRequested();
                _store.Update(s => s with { SpokenChecklistItem = item.Say });

                var completed = item.Behavior.ToLowerInvariant() switch
                {
                    "verify" => await RunVerifyAsync(item, ct).ConfigureAwait(false),
                    "action" => await RunActionAsync(item, ct).ConfigureAwait(false),
                    "monitorcontrols" => await RunMonitorControlsAsync(item, ct).ConfigureAwait(false),
                    // captureMinima degrades to acknowledge until the briefing flow owns it.
                    _ => await RunAcknowledgeAsync(item, ct).ConfigureAwait(false),
                };
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
        await Speak(item.Say).ConfigureAwait(false);
        var answer = await AwaitAcceptedAsync(item, ct).ConfigureAwait(false);
        if (answer is null)
        {
            return false;
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
            var skipped = await MonitorPhaseAsync(item, ct).ConfigureAwait(false);
            if (skipped)
            {
                await Speak("Flight controls check, skipped.").ConfigureAwait(false);
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

    /// <summary>Runs the captain-sweep watch with a command-only window open so skip/cancel
    /// still work. Returns true when the pilot skipped.</summary>
    private async Task<bool> MonitorPhaseAsync(ChecklistItemDefinition item, CancellationToken ct)
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
            return false;
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

        _recognition.OpenListeningWindow(VoiceCommands.All);
        try
        {
            var completed = await _monitor.RunAsync(
                spec, text => Speak(text), () => false, skip.Token).ConfigureAwait(false);
            return !completed && !ct.IsCancellationRequested;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return true; // skip cancelled the monitor, not the run
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
                // Gray band: without a dedicated confirm sub-dialogue yet, treat as not
                // caught — the pilot repeats. (The predecessor asked "did you mean …?".)
                CompleteResponse(new EngineResponse(ResponseKind.NotCaught, ""));
                return;
        }

        var text = interpretation.Text;

        // An awaiting item's accepted phrase outranks the identically-named global command.
        if (awaiting is not null && IsAccepted(awaiting, text)
            && awaiting.AcceptedPhrases.Any(p => CommandMatcher.Normalize(p)
                .Equals(CommandMatcher.Normalize(text), StringComparison.Ordinal)))
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

            case "cancel checklist":
                Cancel();
                return;

            case "restart checklist":
                RestartActive();
                return;
        }

        if (awaiting is not null)
        {
            CompleteResponse(new EngineResponse(ResponseKind.Phrase, text));
            return;
        }

        // Idle window: a checklist start phrase?
        foreach (var definition in _checklists.Definitions())
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

    private List<string> BuildRouteVocabulary(ChecklistItemDefinition? awaiting)
    {
        var vocabulary = new List<string>(VoiceCommands.All);
        if (awaiting is not null)
        {
            vocabulary.AddRange(awaiting.AcceptedPhrases);
        }
        else
        {
            foreach (var definition in _checklists.Definitions())
            {
                vocabulary.AddRange(definition.StartPhrases is { Count: > 0 }
                    ? definition.StartPhrases
                    : [$"{definition.Checklist} checklist"]);
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
