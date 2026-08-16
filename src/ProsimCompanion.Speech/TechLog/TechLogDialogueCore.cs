using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.TechLog;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.TechLog;

/// <summary>
/// The speak/listen surface the dialogue core drives — production adapts it onto the arbiter
/// and <see cref="IMicOwnership"/>; tests script it. SpeakAsync must resolve only at the
/// prompt's terminal outcome, because the core opens each listen window strictly after the
/// preceding prompt finishes.
/// </summary>
public interface ITechLogDialogueIo
{
    /// <summary>Speaks a prompt, resolving at its terminal outcome (spoken/dropped/…).</summary>
    Task SpeakAsync(string text, CancellationToken cancellationToken);

    /// <summary>Listens with the given grammar (EMPTY grammar = free-form raw transcription)
    /// and resolves with the utterance, or null on timeout.</summary>
    Task<string?> ListenAsync(IReadOnlyList<string> grammar, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>
/// The tech-log dialogue state machines (Prosim2FO's RaiseByVoice / RectifyByVoice /
/// OfferAbnormals semantics, re-implemented): pure over the injected speak/listen seam so
/// prompt sequences, category mapping and the confirm/deny/timeout branches are testable with
/// scripted listen results — no recognizers, timers or audio. Mic ownership, enable-gating and
/// background scheduling live in <see cref="TechLogDialogueService"/>.
/// </summary>
public sealed class TechLogDialogueCore
{
    /// <summary>Free-form title capture window — dictating a defect takes longer than a command.</summary>
    public static readonly TimeSpan TitleTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Closed-grammar windows (category, confirmations).</summary>
    public static readonly TimeSpan PromptTimeout = TimeSpan.FromSeconds(8);

    /// <summary>Category answers; <see cref="ParseCategory"/> contains-maps them, so a raw
    /// whisper transcription like "make it category bravo" still lands.</summary>
    public static readonly IReadOnlyList<string> CategoryGrammar =
    [
        "alpha", "bravo", "charlie", "delta",
        "category alpha", "category bravo", "category charlie", "category delta",
    ];

    private readonly ITechLogService _techLog;
    private readonly ITechLogDialogueIo _io;
    private readonly JsonlEventLog _eventLog;

    public TechLogDialogueCore(ITechLogService techLog, ITechLogDialogueIo io, JsonlEventLog eventLog)
    {
        ArgumentNullException.ThrowIfNull(techLog);
        ArgumentNullException.ThrowIfNull(io);
        ArgumentNullException.ThrowIfNull(eventLog);

        _techLog = techLog;
        _io = io;
        _eventLog = eventLog;
    }

    /// <summary>Guided defect raise: free-form title → MEL category → read-back confirm →
    /// raise. Any non-affirm (negative, unrelated, timeout) discards without side effects.</summary>
    public async Task RunRaiseAsync(CancellationToken cancellationToken)
    {
        await _io.SpeakAsync("Go ahead with the defect.", cancellationToken).ConfigureAwait(false);

        // EMPTY grammar: the raw transcription passes through with no command snapping.
        var title = await _io.ListenAsync([], TitleTimeout, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(title))
        {
            await _io.SpeakAsync(
                "I didn't catch that — you can enter it on the tech log page.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await _io.SpeakAsync("What M E L category — Bravo, Charlie, or Delta?", cancellationToken)
            .ConfigureAwait(false);
        var category = ParseCategory(
            await _io.ListenAsync(CategoryGrammar, PromptTimeout, cancellationToken).ConfigureAwait(false));
        var days = _techLog.RepairDaysForCategory(category);

        await _io.SpeakAsync(
            $"Logging {title}. M E L category {SpokenCategory(category)}, {days} day interval. Confirm?",
            cancellationToken).ConfigureAwait(false);
        var confirm = await _io.ListenAsync(ConfirmVocabulary.All, PromptTimeout, cancellationToken)
            .ConfigureAwait(false);
        if (!IsAffirm(confirm))
        {
            await _io.SpeakAsync("Disregarded — nothing entered.", cancellationToken).ConfigureAwait(false);
            return;
        }

        _techLog.RaiseDefect(_techLog.NewDraft("manual", title, category));
        await _io.SpeakAsync($"Tech log entry made. {days} days to rectify.", cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Guided rectification of the most-due open defect (yes/no). A clean log answers
    /// and stops; only an explicit affirm actions the maintenance.</summary>
    public async Task RunRectifyAsync(CancellationToken cancellationToken)
    {
        var open = _techLog.OpenDefects;
        if (open.Count == 0)
        {
            await _io.SpeakAsync(
                "The tech log is already clean — nothing to rectify.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var target = open[0]; // OpenDefects is most-due first
        await _io.SpeakAsync($"Rectify {target.Title}? Affirm or negative.", cancellationToken)
            .ConfigureAwait(false);
        var confirm = await _io.ListenAsync(ConfirmVocabulary.All, PromptTimeout, cancellationToken)
            .ConfigureAwait(false);
        if (!IsAffirm(confirm))
        {
            await _io.SpeakAsync("Left open.", cancellationToken).ConfigureAwait(false);
            return;
        }

        _techLog.RectifyDefect(target.Id);
        await _io.SpeakAsync(
            $"Maintenance actioned. {target.Title} — tech log entry cleared.", cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Offers to log each abnormal handled this flight (id → title), skipping any
    /// already raised this flight (source <c>fromAbnormal:&lt;id&gt;</c> with a matching
    /// RaisedFlight — the same abnormal from an earlier flight is offered again). Every offer
    /// asked is JSONL-recorded as <c>techlog.offer</c> with the pilot's answer.</summary>
    public async Task RunAbnormalOffersAsync(
        IReadOnlyList<KeyValuePair<string, string>> abnormals,
        string? sessionId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(abnormals);

        foreach (var (id, title) in abnormals)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var source = "fromAbnormal:" + id;
            if (_techLog.Defects.Any(d =>
                    string.Equals(d.Source, source, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(d.RaisedFlight, sessionId, StringComparison.OrdinalIgnoreCase)))
            {
                continue; // already logged this flight — never offer twice
            }

            await _io.SpeakAsync(
                $"We had the {title} this flight. Shall I enter it in the tech log? Affirm or negative.",
                cancellationToken).ConfigureAwait(false);
            var accepted = IsAffirm(
                await _io.ListenAsync(ConfirmVocabulary.All, PromptTimeout, cancellationToken)
                    .ConfigureAwait(false));
            _eventLog.Record("techlog.offer", new { id, title, accepted });
            if (!accepted)
            {
                continue;
            }

            _techLog.RaiseDefect(_techLog.NewDraft(source, title, MelCategory.C));
            await _io.SpeakAsync("Entered in the tech log, category Charlie.", cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Contains-mapping (raw transcriptions arrive unsnapped): alpha → A, bravo → B,
    /// delta → D; charlie, timeout and anything unclear default to C — the safest, most common
    /// deferral category.</summary>
    public static MelCategory ParseCategory(string? said)
    {
        var text = said?.ToLowerInvariant() ?? "";
        if (text.Contains("alpha", StringComparison.Ordinal))
        {
            return MelCategory.A;
        }

        if (text.Contains("bravo", StringComparison.Ordinal))
        {
            return MelCategory.B;
        }

        if (text.Contains("delta", StringComparison.Ordinal))
        {
            return MelCategory.D;
        }

        return MelCategory.C;
    }

    /// <summary>NATO word for the read-back confirm prompt (shared table, issue #68). The
    /// enum names ARE the letters, so the word lookup can never miss; the fallback only
    /// guards a future non-letter member.</summary>
    public static string SpokenCategory(MelCategory category)
        => Core.Speech.NatoPhonetics.Word(category.ToString()[0]) ?? category.ToString();

    /// <summary>Affirm iff an affirm word matches exactly or as a whole word ("yes please"
    /// affirms). Null/timeout, negatives and unrelated speech all read as non-affirm — a
    /// destructive-adjacent action needs an explicit yes.</summary>
    public static bool IsAffirm(string? said)
    {
        if (string.IsNullOrWhiteSpace(said))
        {
            return false;
        }

        var normalized = CommandMatcher.Normalize(said);
        foreach (var phrase in ConfirmVocabulary.Affirm)
        {
            var affirm = CommandMatcher.Normalize(phrase);
            if (normalized.Equals(affirm, StringComparison.Ordinal)
                || $" {normalized} ".Contains($" {affirm} ", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
