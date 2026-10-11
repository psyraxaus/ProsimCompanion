using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Hosting;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Gsx;

/// <summary>
/// The FO's "Next plan loaded — commence ground services?" question (issue #169): with
/// <c>gsx.groundPrepActivation = voiceThenAuto</c> and <c>gsx.turnaroundAskFirst</c> on, the
/// GSX automation raises a <see cref="TurnaroundStartQuestion"/> once the next leg's OFP has
/// settled at the stand; this dialogue voices it, borrows the mic, listens for yes/no and
/// answers the Core store — yes starts the departure sequence as a voice start, no (or
/// nobody answering within two listens) leaves the voice gate exactly as it was, so
/// "commence ground services" and the Start button still work. Same shape as
/// <see cref="DeiceQuestionDialogue"/>. Startup module: the host calls <see cref="Start"/>.
/// </summary>
public sealed class TurnaroundStartDialogue : IStartupModule, IDisposable
{
    private static readonly TimeSpan ListenTimeout = TimeSpan.FromSeconds(12);
    private const int MaxAttempts = 2;

    private static readonly IReadOnlyList<string> Grammar =
    [
        .. ConfirmVocabulary.Affirm, .. ConfirmVocabulary.Negative, .. ConfirmVocabulary.SayAgain,
        .. GsxVoicePhrases.StartGroundServicesPhrases,
        "go ahead", "commence", "standby", "stand by", "not yet", "hold", "wait", "cancel",
    ];

    private readonly TurnaroundStartStore _store;
    private readonly IMicOwnership _mic;
    private readonly ISpeechArbiter _arbiter;
    private readonly IOptionsMonitor<GsxOptions> _options;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<TurnaroundStartDialogue> _logger;
    private int _running;

    public TurnaroundStartDialogue(
        TurnaroundStartStore store,
        IMicOwnership mic,
        ISpeechArbiter arbiter,
        IOptionsMonitor<GsxOptions> options,
        JsonlEventLog eventLog,
        ILogger<TurnaroundStartDialogue> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(mic);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _mic = mic;
        _arbiter = arbiter;
        _options = options;
        _eventLog = eventLog;
        _logger = logger;
    }

    public void Start() => _store.QuestionRaised += OnQuestionRaised;

    public void Dispose() => _store.QuestionRaised -= OnQuestionRaised;

    private void OnQuestionRaised(object? sender, TurnaroundStartQuestion question) => _ = RunAsync(question);

    /// <summary>Classifies one heard utterance: true = commence, false = hold, null = not an
    /// answer (say again / unclear). Pure, for tests.</summary>
    public static bool? Interpret(string heard)
    {
        ArgumentNullException.ThrowIfNull(heard);
        var text = CommandMatcher.Normalize(heard);
        if (text.Contains("not yet", StringComparison.Ordinal) || text.Contains("stand by", StringComparison.Ordinal)
            || text.Contains("standby", StringComparison.Ordinal) || text == "hold" || text == "wait" || text == "cancel")
        {
            return false;
        }

        if (text.Contains("ground services", StringComparison.Ordinal) || text.Contains("go ahead", StringComparison.Ordinal)
            || text.Contains("commence", StringComparison.Ordinal))
        {
            return true;
        }

        if (ConfirmVocabulary.Negative.Any(n => text == n || text.StartsWith(n + " ", StringComparison.Ordinal)))
        {
            return false;
        }

        if (ConfirmVocabulary.Affirm.Any(a => text == a || text.StartsWith(a + " ", StringComparison.Ordinal)))
        {
            return true;
        }

        return null;
    }

    /// <summary>Runs one question; public so tests can drive it directly.</summary>
    public async Task RunAsync(TurnaroundStartQuestion question, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(question);
        if (!_options.CurrentValue.VoiceControlEnabled)
        {
            return; // the Start button answers it instead
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
                scope = _mic.Borrow("turnaroundStart");
            }
            catch (InvalidOperationException)
            {
                _logger.LogDebug("Turnaround start question skipped — microphone already borrowed");
                return;
            }

            using (scope)
            {
                _eventLog.Record("turnaround-start-question", new { prompt = question.Prompt });
                await _arbiter.SpeakAsync(question.Prompt, SpeechPriority.High, cancellationToken).ConfigureAwait(false);

                for (var attempt = 0; attempt < MaxAttempts; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!_store.Snapshot().QuestionOpen)
                    {
                        return; // answered elsewhere (Start button, "commence ground services")
                    }

                    var heard = await _mic.ListenAsync(Grammar, ListenTimeout, cancellationToken).ConfigureAwait(false);
                    if (heard is null)
                    {
                        continue;
                    }

                    var text = CommandMatcher.Normalize(heard);
                    if (ConfirmVocabulary.SayAgain.Any(s => text.Contains(s, StringComparison.Ordinal)))
                    {
                        await _arbiter.SpeakAsync(question.Prompt, SpeechPriority.High, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    switch (Interpret(heard))
                    {
                        case true:
                            _eventLog.Record("turnaround-start-question", new { outcome = "accepted", heard });
                            _store.Accept("voice");
                            await _arbiter.SpeakAsync("Copied — commencing ground services.", SpeechPriority.High, cancellationToken).ConfigureAwait(false);
                            return;
                        case false:
                            _eventLog.Record("turnaround-start-question", new { outcome = "declined", heard });
                            _store.Decline("voice", $"captain said '{heard}'");
                            await _arbiter.SpeakAsync("Copied — standing by for your call.", SpeechPriority.High, cancellationToken).ConfigureAwait(false);
                            return;
                        default:
                            await _arbiter.SpeakAsync("Say again — commence ground services, or stand by?", SpeechPriority.High, cancellationToken).ConfigureAwait(false);
                            continue;
                    }
                }

                _eventLog.Record("turnaround-start-question", new { outcome = "unanswered" });
                _store.Decline("voice", "no answer to the FO's question");
                await _arbiter.SpeakAsync(
                    "Your call — say commence ground services when you are ready.",
                    SpeechPriority.Normal, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Turnaround start question dialogue failed");
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }
}
