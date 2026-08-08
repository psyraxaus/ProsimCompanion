using ProsimCompanion.Core.Checklists;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Abnormals;

/// <summary>
/// The speak/listen/verify surface the ECAM dialogue core drives — production adapts it onto
/// the arbiter, <see cref="IMicOwnership"/> and the dataref cache; tests script it. SpeakAsync
/// must resolve only at the prompt's terminal outcome, because the core opens each listen
/// window strictly after the preceding prompt finishes.
/// </summary>
public interface IEcamDialogueIo
{
    /// <summary>Speaks a prompt, resolving at its terminal outcome (spoken/dropped/…).</summary>
    Task SpeakAsync(string text, CancellationToken cancellationToken);

    /// <summary>Listens with the given grammar and resolves with the recognized utterance
    /// (trimmed), or null on timeout.</summary>
    Task<string?> ListenAsync(IReadOnlyList<string> grammar, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Evaluates a dataref condition against the live aircraft; null when the
    /// condition cannot be read right now (disconnected/stale/no value yet) — the dialogue
    /// then asks the pilot instead of pretending to know.</summary>
    bool? TryEvaluate(VerifyCondition condition);
}

/// <summary>
/// The interactive ECAM abnormal dialogue state machine (Prosim2FO
/// <c>AbnormalProcedureEngine</c> semantics, re-implemented): each action line is spoken and
/// then <b>gated on the pilot</b> — the line's own <c>confirm</c> phrases (or any
/// <see cref="ConfirmVocabulary.Affirm"/> word) advance it; "standby" pauses until a continue
/// phrase; "say again" repeats; "skip" abandons the line. Lines with a <c>verify</c> dataref
/// are checked after the pilot confirms, re-prompting a discrepancy before offering
/// continue-unverified/standby; lines with a branch <c>condition</c> are skipped when it reads
/// false and referred to the pilot when it cannot be read. Status lines and the closing call
/// are read straight through. Pure over the injected speak/listen/verify seam so the whole
/// sequencing is testable with scripted answers — no recognizers, timers or audio. Mic
/// ownership, announcement, serialization and skip-if-cleared live in
/// <see cref="FailureMonitor"/>. Detect-and-report only: the FO never actuates anything.
/// </summary>
public sealed class EcamDialogueCore
{
    /// <summary>Pilot-response window per prompt. Prosim2FO's
    /// <c>abnormals.confirmTimeoutSeconds</c> default (20 s, clamped ≥ 3 there); a constant
    /// here until an abnormals options class exists.</summary>
    public static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Re-poll window while standing by — the FO listens silently in these slices
    /// (no re-prompt between them) until a continue phrase arrives.</summary>
    public static readonly TimeSpan StandbyPollTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Verification mismatches tolerated before offering continue/standby
    /// (Prosim2FO's <c>abnormals.maxVerifyRetries</c> default).</summary>
    public const int MaxVerifyRetries = 1;

    /// <summary>Consecutive unanswered line windows before the FO stops re-prompting and
    /// stands by on its own. Deliberate deviation from Prosim2FO, which re-prompted "Say
    /// again, or say standby." forever: a busy (or recognizer-less) flight deck gets three
    /// nudges, then a quiet resumable standby instead of a nag loop.</summary>
    public const int MaxSilentPrompts = 3;

    // Global dialogue vocabulary — carried verbatim from Prosim2FO AbnormalProcedureEngine.
    internal static readonly string[] StandbyPhrases = ["standby", "stand by"];
    internal static readonly string[] ContinuePhrases = ["continue", "continue ecam", "continue checklist", "proceed"];
    internal static readonly string[] SayAgainPhrases = ["say again", "repeat"];
    internal static readonly string[] SkipPhrases = ["skip", "skip this", "skip line"];

    private enum LineInput
    {
        None,
        Confirm,
        Standby,
        SayAgain,
        Skip,
    }

    private readonly IEcamDialogueIo _io;
    private readonly JsonlEventLog _eventLog;

    public EcamDialogueCore(IEcamDialogueIo io, JsonlEventLog eventLog)
    {
        ArgumentNullException.ThrowIfNull(io);
        ArgumentNullException.ThrowIfNull(eventLog);

        _io = io;
        _eventLog = eventLog;
    }

    /// <summary>
    /// Runs the full dialogue for an already-announced ECAM procedure: branch-gated action
    /// lines one at a time (each confirm-gated), then the status review straight through, then
    /// the closing call. Cancellation (shutdown) simply stops mid-line.
    /// </summary>
    public async Task RunAsync(AbnormalDefinition definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);

        _eventLog.Record("abnormal.started", new { id = definition.Id, title = definition.Title });

        foreach (var line in definition.Actions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await ShouldApplyLineAsync(definition, line, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            await RunLineAsync(definition, line, cancellationToken).ConfigureAwait(false);
        }

        // The STATUS review is informational — read straight through, no confirmation.
        foreach (var status in definition.Status)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _io.SpeakAsync(status, cancellationToken).ConfigureAwait(false);
        }

        await _io.SpeakAsync("ECAM actions complete. Resuming normal duties.", cancellationToken)
            .ConfigureAwait(false);

        // Optional sanity check that the actions actually addressed the failure — reported,
        // never re-litigated with the pilot.
        var cleared = definition.ClearedWhen is null
            || _io.TryEvaluate(definition.ClearedWhen) == true;
        _eventLog.Record("abnormal.completed", new { id = definition.Id, title = definition.Title, cleared });
    }

    /// <summary>One action line: speak, wait for the pilot, and only leave on confirm
    /// (verified when authored), skip, or a continue-past-unverified decision.</summary>
    private async Task RunLineAsync(AbnormalDefinition definition, AbnormalAction line, CancellationToken cancellationToken)
    {
        var grammar = BuildLineGrammar(line);
        var discrepancies = 0;
        var silentPrompts = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _io.SpeakAsync(line.Say, cancellationToken).ConfigureAwait(false);

            var said = await _io.ListenAsync(grammar, ConfirmTimeout, cancellationToken).ConfigureAwait(false);
            switch (Classify(said, line))
            {
                case LineInput.Standby:
                    silentPrompts = 0;
                    await StandbyAsync(definition, "standby", cancellationToken).ConfigureAwait(false);
                    continue; // re-speak the line once resumed

                case LineInput.SayAgain:
                    silentPrompts = 0;
                    continue;

                case LineInput.Skip:
                    _eventLog.Record("abnormal.line", new { id = definition.Id, say = line.Say, outcome = "skipped" });
                    return;

                case LineInput.None:
                    silentPrompts++;
                    if (silentPrompts >= MaxSilentPrompts)
                    {
                        silentPrompts = 0;
                        await StandbyAsync(definition, "silence", cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    await _io.SpeakAsync("Say again, or say standby.", cancellationToken).ConfigureAwait(false);
                    continue;

                case LineInput.Confirm:
                    silentPrompts = 0;
                    if (line.Verify is null)
                    {
                        await AckAsync(line, cancellationToken).ConfigureAwait(false);
                        _eventLog.Record("abnormal.line",
                            new { id = definition.Id, say = line.Say, confirmed = true, verified = (bool?)null });
                        return;
                    }

                    // Unreadable (null) counts as unverified — the FO never claims to have
                    // checked what it could not read.
                    var verified = _io.TryEvaluate(line.Verify) == true;
                    _eventLog.Record("abnormal.line",
                        new { id = definition.Id, say = line.Say, confirmed = true, verified });
                    if (verified)
                    {
                        await AckAsync(line, cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    await _io.SpeakAsync(
                        line.Discrepancy ?? $"I don't yet have {line.Say} confirmed.", cancellationToken)
                        .ConfigureAwait(false);
                    discrepancies++;
                    if (discrepancies > MaxVerifyRetries)
                    {
                        if (await OfferStandbyAsync(cancellationToken).ConfigureAwait(false))
                        {
                            await StandbyAsync(definition, "standby", cancellationToken).ConfigureAwait(false);
                            discrepancies = 0;
                            continue;
                        }

                        // Continue, or no answer → proceed despite the unverified state (logged).
                        _eventLog.Record("abnormal.line",
                            new { id = definition.Id, say = line.Say, outcome = "continued-unverified" });
                        return;
                    }

                    continue; // re-speak and retry
            }
        }
    }

    /// <summary>Branch gate: a line with a <c>condition</c> is presented only when it reads
    /// true; an unreadable gate is referred to the pilot rather than guessed (timeout or a
    /// negative both skip).</summary>
    private async Task<bool> ShouldApplyLineAsync(AbnormalDefinition definition, AbnormalAction line, CancellationToken cancellationToken)
    {
        if (line.Condition is null)
        {
            return true;
        }

        var byDataref = _io.TryEvaluate(line.Condition);
        if (byDataref is bool decided)
        {
            _eventLog.Record("abnormal.branch",
                new { id = definition.Id, say = line.Say, apply = decided, by = "dataref" });
            return decided;
        }

        await _io.SpeakAsync($"Regarding: {line.Say}. Does this apply? Affirm or negative.", cancellationToken)
            .ConfigureAwait(false);
        var said = await _io.ListenAsync(ConfirmVocabulary.All, ConfirmTimeout, cancellationToken)
            .ConfigureAwait(false);
        var apply = said is not null
            && ConfirmVocabulary.Affirm.Contains(said.Trim(), StringComparer.OrdinalIgnoreCase);
        _eventLog.Record("abnormal.branch",
            new { id = definition.Id, say = line.Say, apply, by = "pilot" });
        return apply;
    }

    /// <summary>Pauses until the pilot says any continue phrase, listening silently in
    /// <see cref="StandbyPollTimeout"/> slices — the FO speaks once going in and once coming
    /// out, never in between.</summary>
    private async Task StandbyAsync(AbnormalDefinition definition, string reason, CancellationToken cancellationToken)
    {
        _eventLog.Record("abnormal.paused", new { id = definition.Id, reason });
        await _io.SpeakAsync("Standing by. Say continue ECAM when ready.", cancellationToken).ConfigureAwait(false);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The grammar is only the continue phrases, so any recognition resumes.
            var said = await _io.ListenAsync(ContinuePhrases, StandbyPollTimeout, cancellationToken)
                .ConfigureAwait(false);
            if (said is not null)
            {
                break;
            }
        }

        _eventLog.Record("abnormal.resumed", new { id = definition.Id });
        await _io.SpeakAsync("Continuing.", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>After repeated verification mismatches: continue past the line, or stand by?
    /// Returns true for standby; a continue phrase <b>or a timeout</b> proceeds unverified —
    /// the dialogue never wedges on a line the aircraft disagrees with.</summary>
    private async Task<bool> OfferStandbyAsync(CancellationToken cancellationToken)
    {
        await _io.SpeakAsync("Say continue to proceed, or standby.", cancellationToken).ConfigureAwait(false);
        var grammar = ContinuePhrases.Concat(StandbyPhrases).ToList();
        var said = await _io.ListenAsync(grammar, ConfirmTimeout, cancellationToken).ConfigureAwait(false);
        return said is not null && StandbyPhrases.Contains(said.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    private Task AckAsync(AbnormalAction line, CancellationToken cancellationToken)
        => string.IsNullOrWhiteSpace(line.Ack)
            ? Task.CompletedTask
            : _io.SpeakAsync(line.Ack, cancellationToken);

    /// <summary>Per-line grammar: the line's own confirm phrases, the generic affirmatives,
    /// and the global dialogue words.</summary>
    internal static List<string> BuildLineGrammar(AbnormalAction line)
        => line.Confirm
            .Concat(ConfirmVocabulary.Affirm)
            .Concat(StandbyPhrases)
            .Concat(SayAgainPhrases)
            .Concat(SkipPhrases)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Global words win over confirm phrases (so a JSON author cannot shadow
    /// "standby"); anything else — including a timeout — is <see cref="LineInput.None"/>.</summary>
    private static LineInput Classify(string? said, AbnormalAction line)
    {
        if (string.IsNullOrWhiteSpace(said))
        {
            return LineInput.None;
        }

        var text = said.Trim();
        if (StandbyPhrases.Contains(text, StringComparer.OrdinalIgnoreCase))
        {
            return LineInput.Standby;
        }

        if (SayAgainPhrases.Contains(text, StringComparer.OrdinalIgnoreCase))
        {
            return LineInput.SayAgain;
        }

        if (SkipPhrases.Contains(text, StringComparer.OrdinalIgnoreCase))
        {
            return LineInput.Skip;
        }

        if (line.Confirm.Any(p => string.Equals(p.Trim(), text, StringComparison.OrdinalIgnoreCase))
            || ConfirmVocabulary.Affirm.Contains(text, StringComparer.OrdinalIgnoreCase))
        {
            return LineInput.Confirm;
        }

        return LineInput.None;
    }
}
