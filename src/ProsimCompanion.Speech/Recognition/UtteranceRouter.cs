using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Checklists;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Checklists;

namespace ProsimCompanion.Speech.Recognition;

/// <summary>How the router resolved an utterance that belongs to the checklist run loop.</summary>
public enum RoutedResponseKind
{
    /// <summary>A phrase for the awaiting item (or the hold loop) to judge.</summary>
    Phrase,

    /// <summary>"skip" with no control monitor owning it.</summary>
    Skip,

    SayAgain,

    /// <summary>Rejected/uncaught while a response was pending — the item's flow answers.</summary>
    NotCaught,

    Hold,
}

/// <summary>The checklist run loop's surface as the router sees it — the narrow seam
/// (campaign #82) that lets routing be tested without standing up a checklist engine.</summary>
public interface IChecklistRoutingHost
{
    /// <summary>The item currently awaiting its answer, if any.</summary>
    ChecklistItemDefinition? AwaitingItem { get; }

    /// <summary>True while any response source (item window or hold loop) is pending.</summary>
    bool ResponsePending { get; }

    /// <summary>True when fully idle — no run, no awaiting item, no pending response.</summary>
    bool IsIdle { get; }

    /// <summary>Cancels a control-monitor skip when one owns "skip"; false otherwise.</summary>
    bool TryCancelMonitorSkip();

    /// <summary>Whether the text is an accepted answer for the item (never the display-only
    /// ExpectedResponse).</summary>
    bool IsAcceptedAnswer(ChecklistItemDefinition item, string text);

    /// <summary>Delivers a routed response to the pending source (no-op when none).</summary>
    void Complete(RoutedResponseKind kind, string text);

    void CancelChecklist();

    void RestartActive();

    void StartChecklist(string name);
}

/// <summary>
/// The utterance router (CONTEXT.md): decides what a recognized utterance means right now —
/// value-parse feature, checklist answer, global command, voice feature, memory drill,
/// checklist start, gray-band confirmation, or idle miss — by the fixed precedence order the
/// predecessor established. Extracted from <see cref="SpokenChecklistEngine"/> (campaign #82):
/// the engine keeps the checklist run loop and exposes it through
/// <see cref="IChecklistRoutingHost"/>; the router owns interpretation, the closed grammar
/// (disabled features excluded), and the idle listening window.
/// </summary>
public sealed class UtteranceRouter : IDisposable
{
    private readonly UtteranceInterpreter _interpreter;
    private readonly ChecklistService _checklists;
    private readonly Abnormals.FailureMonitor _failures;
    private readonly IReadOnlyList<IVoiceFeature> _features;
    private readonly IRecognitionWindow _recognition;
    private readonly IMicOwnership _micOwnership;
    private readonly ISpeechArbiter _arbiter;
    private readonly Persona.PhraseBank _phrases;
    private readonly Persona.PersonaService _persona;
    private readonly LlmHealthStore _llmHealth;
    private readonly ILogger<UtteranceRouter> _logger;

    // Optional (tests construct the router bare): phase-aware reject suppression, issue #67.
    private readonly Core.Flight.IFlightPhaseSource? _flightPhase;
    private IChecklistRoutingHost? _host;
    private bool _started;
    private bool _llmOfflineAdvisoryGiven; // once per session (issue #66)

    public UtteranceRouter(
        UtteranceInterpreter interpreter,
        ChecklistService checklists,
        Abnormals.FailureMonitor failures,
        IEnumerable<IVoiceFeature> features,
        IRecognitionWindow recognition,
        IMicOwnership micOwnership,
        ISpeechArbiter arbiter,
        Persona.PhraseBank phrases,
        Persona.PersonaService persona,
        LlmHealthStore llmHealth,
        ILogger<UtteranceRouter> logger,
        Core.Flight.IFlightPhaseSource? flightPhase = null)
    {
        ArgumentNullException.ThrowIfNull(interpreter);
        ArgumentNullException.ThrowIfNull(checklists);
        ArgumentNullException.ThrowIfNull(failures);
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(recognition);
        ArgumentNullException.ThrowIfNull(micOwnership);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(phrases);
        ArgumentNullException.ThrowIfNull(persona);
        ArgumentNullException.ThrowIfNull(llmHealth);
        ArgumentNullException.ThrowIfNull(logger);

        _interpreter = interpreter;
        _checklists = checklists;
        _failures = failures;
        _features = [.. features];
        _recognition = recognition;
        _micOwnership = micOwnership;
        _arbiter = arbiter;
        _phrases = phrases;
        _persona = persona;
        _llmHealth = llmHealth;
        _logger = logger;
        _flightPhase = flightPhase;
    }

    /// <summary>Attaches the checklist run loop; called by the engine before Start.</summary>
    public void Attach(IChecklistRoutingHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
    }

    /// <summary>Subscribes recognition + mic events and opens the idle window. Driven by the
    /// checklist engine's Start so bootstrap ordering stays exactly as before (#61).</summary>
    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _recognition.Accepted += OnRecognized;
        _recognition.Rejected += OnRejected;
        // A dialogue that borrowed the mic BEFORE this Start (bootstrap ordering — the
        // dialogue services start earlier) captured window-closed and would close our idle
        // window on release; re-assert it after every release instead (issue #61).
        _micOwnership.Released += OnMicReleased;
        OpenIdleWindow();
    }

    public void Dispose()
    {
        if (!_started)
        {
            return;
        }

        _recognition.Accepted -= OnRecognized;
        _recognition.Rejected -= OnRejected;
        _micOwnership.Released -= OnMicReleased;
    }

    /// <summary>Idle grammar: every checklist's start phrases, drill phrases and the enabled
    /// features' phrases — always listening for a start.</summary>
    public void OpenIdleWindow()
        => _recognition.OpenListeningWindow(BuildRouteVocabulary(null));

    /// <summary>Global commands + enabled feature phrases — the control-monitor window's
    /// grammar (skip/cancel and mid-check handovers keep working).</summary>
    public List<string> CommandAndFeaturePhrases()
    {
        var grammar = new List<string>(VoiceCommands.All);
        foreach (var feature in _features.Where(f => f.Enabled))
        {
            grammar.AddRange(feature.Phrases);
        }

        return grammar;
    }

    private void OnRecognized(object? sender, RecognizedEventArgs e)
    {
        // A borrowed mic means a guided dialogue (tech-log raise/rectify, shutdown offer) owns
        // recognition: normal routing — feature dispatch AND checklist answers — stands down.
        // A pending item's response source stays pending, so the checklist holds and resumes
        // when the borrow's disposal replays this router's window.
        if (_micOwnership.IsBorrowed || _host is not { } host)
        {
            return;
        }

        try
        {
            RouteUtterance(host, e);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Utterance routing failed");
        }
    }

    /// <summary>After any mic borrow releases: when the checklist side is fully idle, the idle
    /// grammar is the standing window — re-open it, because the borrow's replay restored
    /// whatever was captured at borrow time, which may pre-date Start. Mid-run states leave
    /// the replayed window alone: an awaiting item's window was captured correctly, and the
    /// run loop opens its own next window.</summary>
    private void OnMicReleased()
    {
        if (_host is { IsIdle: true })
        {
            OpenIdleWindow();
        }
    }

    private void OnRejected(object? sender, RecognizedEventArgs e)
    {
        if (_micOwnership.IsBorrowed || _host is not { } host)
        {
            return; // a dialogue owns the mic — its own listens handle rejection by timeout
        }

        // Empty-text rejections (silent PTT tap, timeout) are swallowed by design.
        if (!string.IsNullOrWhiteSpace(e.Text))
        {
            host.Complete(RoutedResponseKind.NotCaught, "");
        }
    }

    private void RouteUtterance(IChecklistRoutingHost host, RecognizedEventArgs e)
    {
        var awaiting = host.AwaitingItem;
        var responsePending = host.ResponsePending;

        // Value-parsing features (FCU, radios) get the RAW transcription first so numbers
        // survive — skipped while an item is awaiting an answer.
        if (awaiting is null)
        {
            foreach (var feature in _features.Where(f => f.Enabled && f.ValueParse))
            {
                if (feature.TryHandle(e.Text))
                {
                    return;
                }
            }
        }

        var grammar = ScopeForChecklistStart(e.Text, BuildRouteVocabulary(awaiting), awaiting is not null);
        var interpretation = _interpreter.Interpret(
            e.Text, grammar, new InterpretContext(awaiting is not null, e.AcousticConfidence, e.NoSpeechProb));

        // The ASR decision trail (issue #66): local data, one Debug line per utterance —
        // without it, a flight's worth of misrouted utterances left no evidence at all.
        _logger.LogDebug(
            "ASR heard \"{Heard}\" (conf {Confidence:F2}, acoustic {Acoustic}, noSpeech {NoSpeech}) -> {Decision} \"{Matched}\" (score {Score:F2})",
            e.Text, e.Confidence, e.AcousticConfidence, e.NoSpeechProb,
            interpretation.Kind, interpretation.Text, interpretation.Score);

        switch (interpretation.Kind)
        {
            case InterpretKind.Reject:
                if (string.IsNullOrWhiteSpace(e.Text))
                {
                    return;
                }

                if (responsePending)
                {
                    // An item (or the hold loop) owns the reply — its own flow speaks.
                    host.Complete(RoutedResponseKind.NotCaught, "");
                }
                else
                {
                    HandleIdleMiss();
                }

                return;

            case InterpretKind.Confirm:
                // Gray band (score 0.70–0.85, command windows only): "did you mean …?" — the
                // predecessor's affirm-gated recovery instead of silently discarding it.
                _ = ConfirmAndRouteAsync(host, interpretation.Text);
                return;
        }

        RouteText(host, interpretation.Text, awaiting);
    }

    /// <summary>An utterance nothing routed while fully idle (issue #66): previously it fell
    /// into the FCU's "which field?" clarifier or vanished silently. Now it gets the normal
    /// did-not-catch line — or, once per session while the LLM is known-unhealthy, the
    /// advisory that explains WHY free-form phrasing is falling flat.</summary>
    private void HandleIdleMiss()
    {
        // Sterile-phase suppression (issue #67, 2026-08-22 flight): during the takeoff roll
        // and rotation the pilot makes SOP callouts ("takeoff", the FMA readback) faster than
        // features can grow to answer them — a chirped "Didn't catch that" DURING ROTATION is
        // the worst possible chatter. Unmatched speech in these phases is absorbed silently;
        // the ASR decision trail still records every word for the post-flight review.
        if (_flightPhase?.CurrentPhase is Core.Flight.FlightPhase.TakeoffRoll
            or Core.Flight.FlightPhase.InitialClimb
            or Core.Flight.FlightPhase.LandingRollout)
        {
            _logger.LogDebug("Idle miss absorbed silently (sterile phase {Phase})", _flightPhase.CurrentPhase);
            return;
        }

        var response = IdleMissPolicy.Decide(
            _llmHealth.Snapshot().State, _llmOfflineAdvisoryGiven,
            _persona.Acknowledge(Persona.AckKind.DidNotCatch, _phrases.NextDidNotCatch()));
        if (response.IsLlmOfflineAdvisory)
        {
            _llmOfflineAdvisoryGiven = true;
        }

        _ = _arbiter.EnqueueAsync(new SpeechRequest(
            response.Text, SpeechPriority.Normal, Tag: response.Tag));
    }

    /// <summary>Post-interpretation routing (Prosim2FO's Route): an awaiting item's ACCEPTED
    /// answer outranks the identically-named global command; a non-answer falls through to
    /// the global commands and then the voice features, so "my aircraft" or "tune the ils"
    /// still works while a checklist line is pending.</summary>
    private void RouteText(IChecklistRoutingHost host, string text, ChecklistItemDefinition? awaiting)
    {
        if (awaiting is not null && host.IsAcceptedAnswer(awaiting, text))
        {
            host.Complete(RoutedResponseKind.Phrase, text);
            return;
        }

        switch (CommandMatcher.Normalize(text))
        {
            case "skip" or "skip item":
                if (!host.TryCancelMonitorSkip())
                {
                    host.Complete(RoutedResponseKind.Skip, "");
                }

                return;

            case "say again" or "repeat":
                host.Complete(RoutedResponseKind.SayAgain, "");
                return;

            case "hold the checklist" or "standby":
                host.Complete(RoutedResponseKind.Hold, "");
                return;

            case "resume checklist" or "continue":
                // The hold loop owns the pending response while holding; outside a hold this
                // completes into an item answer that fails acceptance (didn't-catch) or
                // no-ops when nothing is pending.
                host.Complete(RoutedResponseKind.Phrase, text);
                return;

            case "cancel checklist":
                host.CancelChecklist();
                return;

            case "restart checklist":
                host.RestartActive();
                return;
        }

        // Voice features stay reachable while an item is pending (reference semantics) — a
        // handover or radio call must not become a failed checklist answer. Disabled
        // features are never offered the utterance (the router owns the gate).
        foreach (var feature in _features.Where(f => f.Enabled))
        {
            if (feature.TryHandle(text))
            {
                return;
            }
        }

        if (awaiting is not null)
        {
            // Not a command, not a feature — treat as the item answer (acceptance already
            // failed above, so this lands in the "didn't catch that" flow).
            host.Complete(RoutedResponseKind.Phrase, text);
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
                host.StartChecklist(definition.Checklist);
                return;
            }
        }

        // Interpreted, yet no command/feature/drill/start claimed it (issue #66): answer
        // like any other idle miss instead of dropping it silently.
        HandleIdleMiss();
    }

    /// <summary>The gray-band recovery (Prosim2FO's ConfirmAndRouteAsync): borrow the mic,
    /// ask "did you mean {candidate}?", listen 8 s on the confirm vocabulary, and route the
    /// candidate only on an affirmative. Anything else (negative, timeout, mic busy) drops it
    /// — the borrow's disposal replays the previous window either way.</summary>
    private async Task ConfirmAndRouteAsync(IChecklistRoutingHost host, string candidate)
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
                await _arbiter.EnqueueAsync(new SpeechRequest(
                    $"Say again — did you mean {candidate}?", SpeechPriority.Normal, Tag: "clarifier"))
                    .ConfigureAwait(false);
                answer = await _micOwnership.ListenAsync(
                    ConfirmVocabulary.All, TimeSpan.FromSeconds(8), CancellationToken.None)
                    .ConfigureAwait(false);
            }

            if (answer is not null
                && ConfirmVocabulary.Affirm.Any(a => answer.Contains(a, StringComparison.OrdinalIgnoreCase)))
            {
                RouteText(host, candidate, host.AwaitingItem);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Confirm dialogue failed for {Candidate}", candidate);
        }
    }

    /// <summary>
    /// Checklist-start bias (issue #38): "approach checklist" phonetically snapped to
    /// "activate approach phase"/"arm localizer" and the Approach checklist never ran. When the
    /// pilot literally said "checklist" outside an item window, snapping is restricted to the
    /// checklist-scoped phrases (start phrases and the global checklist commands) so a start
    /// request can never resolve to an unrelated command. The full grammar stands when nothing
    /// checklist-scoped exists or an item is awaiting its answer.
    /// </summary>
    internal static List<string> ScopeForChecklistStart(string utterance, List<string> grammar, bool itemAwaiting)
    {
        if (itemAwaiting || !utterance.Contains("checklist", StringComparison.OrdinalIgnoreCase))
        {
            return grammar;
        }

        var scoped = grammar
            .Where(p => p.Contains("checklist", StringComparison.OrdinalIgnoreCase))
            .ToList();
        return scoped.Count > 0 ? scoped : grammar;
    }

    private List<string> BuildRouteVocabulary(ChecklistItemDefinition? awaiting)
    {
        var vocabulary = new List<string>(VoiceCommands.All);
        if (awaiting is not null)
        {
            vocabulary.AddRange(awaiting.AcceptedPhrases);
            // Feature phrases stay in the item window (reference semantics): a handover or
            // radio call while a line is pending must snap and dispatch, not fail the item.
            // Disabled features leave the closed grammar entirely.
            foreach (var feature in _features.Where(f => f.Enabled))
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
            foreach (var feature in _features.Where(f => f.Enabled))
            {
                vocabulary.AddRange(feature.Phrases);
            }
        }

        return vocabulary;
    }
}
