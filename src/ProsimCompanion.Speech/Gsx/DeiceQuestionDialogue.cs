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
/// The FO's "request de-icing?" question (2026-10-09): with <c>gsx.deice.autoRequest = ask</c>
/// the GSX policy raises a <see cref="DeiceQuestion"/> when the OAT and the departure METAR
/// call for de-icing; this dialogue voices it ("Captain, conditions call for de-icing — OAT
/// −2, snow. Request it?"), borrows the mic, listens for yes/no and answers the Core store —
/// the sequencer then places or skips the DeIce step.
/// <para>Silence is not an answer (issue #166, EFHK 2026-10-10: 32 s of silence declined the
/// question, and GSX's own offer was then accepted against that decline). After two listens
/// the dialogue hands the decision back with a word and LEAVES THE QUESTION OPEN: the Status
/// board buttons, "request de-icing" / "no de-icing" and GSX's own menu answer it until the
/// store's TTL expires — the only thing that declines on silence. The store re-raises the
/// question once when the de-icing step's turn comes (Attempt 2); this dialogue voices that
/// too. Startup module: construction subscribes; the host calls <see cref="Start"/>.</para>
/// </summary>
public sealed class DeiceQuestionDialogue : IStartupModule, IDisposable
{
    private static readonly TimeSpan ListenTimeout = TimeSpan.FromSeconds(12);
    private const int MaxAttempts = 2;

    /// <summary>What the FO says when nobody answered — the question stands. Probed by
    /// <c>gsx-deice-policy</c>; keep the wording stable.</summary>
    public const string QuestionStandsPhrase =
        "Your call — the question stands. Say request de-icing or no de-icing when you decide, or use the Status board.";

    private static readonly IReadOnlyList<string> Grammar =
    [
        .. ConfirmVocabulary.Affirm, .. ConfirmVocabulary.Negative, .. ConfirmVocabulary.SayAgain,
        "request de-icing", "request deicing", "request de-ice", "no de-icing", "negative de-icing", "not today", "your call", "leave it", "cancel",
    ];

    private readonly DeiceRequestStore _store;
    private readonly IMicOwnership _mic;
    private readonly ISpeechArbiter _arbiter;
    private readonly IOptionsMonitor<GsxOptions> _options;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<DeiceQuestionDialogue> _logger;
    private int _running;

    public DeiceQuestionDialogue(
        DeiceRequestStore store,
        IMicOwnership mic,
        ISpeechArbiter arbiter,
        IOptionsMonitor<GsxOptions> options,
        JsonlEventLog eventLog,
        ILogger<DeiceQuestionDialogue> logger)
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

    private void OnQuestionRaised(object? sender, DeiceQuestion question) => _ = RunAsync(question);

    /// <summary>Classifies one heard utterance: true = request, false = decline, null = not
    /// an answer (say again / unclear). Pure, for tests.</summary>
    public static bool? Interpret(string heard)
    {
        ArgumentNullException.ThrowIfNull(heard);
        var text = CommandMatcher.Normalize(heard);
        if (text.Contains("no de", StringComparison.Ordinal) || text.Contains("not today", StringComparison.Ordinal)
            || text.Contains("negative de", StringComparison.Ordinal) || text.Contains("decline", StringComparison.Ordinal)
            || text.Contains("not required", StringComparison.Ordinal)
            || text.Contains("your call", StringComparison.Ordinal) || text.Contains("leave it", StringComparison.Ordinal)
            || text == "cancel")
        {
            return false;
        }

        if (text.Contains("request", StringComparison.Ordinal) || text.Contains("de ice", StringComparison.Ordinal)
            || text.Contains("deice", StringComparison.Ordinal) || text.Contains("de icing", StringComparison.Ordinal))
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

    /// <summary>The spoken prompt for an attempt: the re-ask when the step's turn has come
    /// says so, because the first ask may be minutes old.</summary>
    public static string SpokenPrompt(DeiceQuestion question)
    {
        ArgumentNullException.ThrowIfNull(question);
        return question.Attempt > 1
            ? $"Captain, de-icing is the last item before pushback and still needs your call. {question.Prompt}"
            : question.Prompt;
    }

    /// <summary>Runs one question; public so tests can drive it directly.</summary>
    public async Task RunAsync(DeiceQuestion question, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(question);
        if (!_options.CurrentValue.VoiceControlEnabled)
        {
            return; // the Status board buttons (and the TTL) answer it instead
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
                scope = _mic.Borrow("deiceQuestion");
            }
            catch (InvalidOperationException)
            {
                _logger.LogDebug("De-ice question skipped — microphone already borrowed");
                return;
            }

            using (scope)
            {
                _eventLog.Record("deice-question", new { prompt = question.Prompt, attempt = question.Attempt });
                await _arbiter.SpeakAsync(SpokenPrompt(question), SpeechPriority.High, cancellationToken).ConfigureAwait(false);

                for (var attempt = 0; attempt < MaxAttempts; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_store.Snapshot().Question is null)
                    {
                        return; // answered elsewhere (web button, direct call, voice command)
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
                            _eventLog.Record("deice-question", new { outcome = "accepted", heard, attempt = question.Attempt });
                            _store.Accept("voice");
                            await _arbiter.SpeakAsync("Copied — requesting de-icing.", SpeechPriority.High, cancellationToken).ConfigureAwait(false);
                            return;
                        case false:
                            _eventLog.Record("deice-question", new { outcome = "declined", heard, attempt = question.Attempt });
                            _store.Decline("voice", $"captain said '{heard}'");
                            await _arbiter.SpeakAsync("Copied — no de-icing.", SpeechPriority.High, cancellationToken).ConfigureAwait(false);
                            return;
                        default:
                            await _arbiter.SpeakAsync("Say again — request de-icing, or negative?", SpeechPriority.High, cancellationToken).ConfigureAwait(false);
                            continue;
                    }
                }

                // Nobody answered: the question stays open on the store (its TTL is the only
                // thing that declines it — #166). The FO says so once and gives the mic back.
                _eventLog.Record("deice-question", new
                {
                    outcome = "unanswered",
                    attempt = question.Attempt,
                    questionOpen = true,
                    expiresAtUtc = _store.Snapshot().Question?.ExpiresAtUtc,
                });
                await _arbiter.SpeakAsync(QuestionStandsPhrase, SpeechPriority.Normal, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "De-ice question dialogue failed");
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }
}
