using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Airports.Parking;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Hosting;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Gsx;

/// <summary>
/// The FO's "which way?" question (2026-10-04): when GSX's direction menu is open and the
/// automation has no choice and no confident suggestion (mode auto + ask-when-unsure, or
/// mode ask), the <see cref="PushbackChoiceStore"/> raises a question and this dialogue
/// voices it — "Pushback: tail left or tail right?" (with the stand's own labels when the
/// profile names them) — borrows the mic, listens for the pilot's answer, stores it and reads
/// it back. Timeout or two misses hand the menu back to the pilot with a word. Startup
/// module: construction subscribes; the host calls <see cref="Start"/>.
/// </summary>
public sealed class PushbackQuestionDialogue : IStartupModule, IDisposable
{
    private static readonly TimeSpan ListenTimeout = TimeSpan.FromSeconds(12);
    private const int MaxAttempts = 2;

    private static readonly IReadOnlyList<string> Grammar =
        [.. PushbackPhraseParser.Phrases, "left", "right", "straight", .. ConfirmVocabulary.SayAgain, "your call", "leave it", "cancel"];

    private readonly PushbackChoiceStore _store;
    private readonly PushbackDirectionVoiceFeature _voice;
    private readonly IMicOwnership _mic;
    private readonly ISpeechArbiter _arbiter;
    private readonly IOptionsMonitor<GsxOptions> _options;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<PushbackQuestionDialogue> _logger;
    private int _running;

    public PushbackQuestionDialogue(
        PushbackChoiceStore store,
        PushbackDirectionVoiceFeature voice,
        IMicOwnership mic,
        ISpeechArbiter arbiter,
        IOptionsMonitor<GsxOptions> options,
        JsonlEventLog eventLog,
        ILogger<PushbackQuestionDialogue> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(voice);
        ArgumentNullException.ThrowIfNull(mic);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _voice = voice;
        _mic = mic;
        _arbiter = arbiter;
        _options = options;
        _eventLog = eventLog;
        _logger = logger;
    }

    public void Start() => _store.QuestionRaised += OnQuestionRaised;

    public void Dispose() => _store.QuestionRaised -= OnQuestionRaised;

    private void OnQuestionRaised(object? sender, PushbackQuestion question) => _ = RunAsync(question);

    /// <summary>"Pushback: tail left or tail right?" — or the stand's own labels when the
    /// profile names them ("Facing SW on Taxi AV, or Facing NE on Taxi AT?"). Public for tests.</summary>
    public static string Prompt(IReadOnlyList<PushbackOption> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var turning = options.Where(o => o.Kind != PushbackOptionKind.Straight).ToList();
        var custom = turning.Where(o => !IsDefaultLabel(o.Label)).Select(o => o.Label).ToList();
        if (custom.Count >= 2)
        {
            return $"Pushback — which way? GSX offers {string.Join(", or ", custom.Take(3))}.";
        }

        var hasLeft = turning.Any(o => o.Kind == PushbackOptionKind.Left);
        var hasRight = turning.Any(o => o.Kind == PushbackOptionKind.Right);
        return hasLeft && hasRight
            ? "Pushback — tail left or tail right?"
            : "Pushback — which way?";
    }

    private static bool IsDefaultLabel(string label)
        => label.Contains("Tail Left", StringComparison.OrdinalIgnoreCase)
            || label.Contains("Tail Right", StringComparison.OrdinalIgnoreCase);

    /// <summary>Runs one question; public so tests can drive it directly.</summary>
    public async Task RunAsync(PushbackQuestion question, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(question);
        if (!_options.CurrentValue.VoiceControlEnabled)
        {
            return;
        }

        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return;
        }

        try
        {
            IDisposable scope;
            try
            {
                scope = _mic.Borrow("pushbackQuestion");
            }
            catch (InvalidOperationException)
            {
                _logger.LogDebug("Pushback question skipped — microphone already borrowed");
                return;
            }

            using (scope)
            {
                var prompt = Prompt(question.Options);
                _eventLog.Record("pushback-question", new { prompt, options = question.Options.Select(o => o.Label).ToList() });
                await _arbiter.SpeakAsync(prompt, SpeechPriority.High, cancellationToken).ConfigureAwait(false);

                for (var attempt = 0; attempt < MaxAttempts; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_store.Snapshot().Question is null)
                    {
                        return; // answered elsewhere (Korry button, menu gone)
                    }

                    var heard = await _mic.ListenAsync(Grammar, ListenTimeout, cancellationToken).ConfigureAwait(false);
                    if (heard is null)
                    {
                        continue;
                    }

                    var text = CommandMatcher.Normalize(heard);
                    if (text.Contains("your call", StringComparison.Ordinal) || text.Contains("leave it", StringComparison.Ordinal) || text == "cancel")
                    {
                        break;
                    }

                    if (ConfirmVocabulary.SayAgain.Any(s => text.Contains(s, StringComparison.Ordinal)))
                    {
                        await _arbiter.SpeakAsync(prompt, SpeechPriority.High, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    var choice = PushbackPhraseParser.Parse(heard)
                        ?? (text == "left" ? PushbackChoice.TailLeft("voice", $"pilot said '{heard}'")
                            : text == "right" ? PushbackChoice.TailRight("voice", $"pilot said '{heard}'")
                            : null);
                    if (choice is null)
                    {
                        await _arbiter.SpeakAsync("Say again — tail left, tail right, or facing which way?", SpeechPriority.High, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    _voice.Apply(choice);
                    return;
                }

                _store.ClearQuestion();
                _eventLog.Record("pushback-question", new { outcome = "unanswered" });
                await _arbiter.SpeakAsync("Your call — the GSX pushback menu is open.", SpeechPriority.Normal, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Pushback question dialogue failed");
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }
}
