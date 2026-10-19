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
/// The FO voices whatever captain's question is open (architecture review C2): one dialogue
/// over every Core <see cref="ICaptainsQuestionStore"/>, driven by one
/// <see cref="CaptainsQuestionPhrases"/> table per question. It borrows the mic, speaks the
/// table's prompt, listens twice (12 s each), hands each utterance to the table's
/// interpretation hook and writes the store — yes/no, a pick, or the table's own settle.
/// <para>Silence is not an answer (issue #166, EFHK 2026-10-10: 32 s of silence declined the
/// de-icing question, and GSX's own offer was then accepted against that decline). After two
/// listens — or "your call" — the FO hands the decision back with a word and LEAVES THE
/// QUESTION OPEN: the web card, the commands, GSX's own menu and the store's TTL settle it.
/// Before C2 that rule held for de-icing alone; the turnaround question declined itself on
/// silence and the pushback question withdrew itself. The store re-raises a question when
/// its raiser asks again (the de-icing step's turn, attempt 2); this dialogue voices that too.
/// Startup module: the host calls <see cref="Start"/>.</para>
/// </summary>
public sealed class CaptainsQuestionDialogue : IStartupModule, IDisposable
{
    /// <summary>One listening window.</summary>
    public static TimeSpan ListenTimeout { get; } = TimeSpan.FromSeconds(12);

    /// <summary>Listens per ask before the FO hands the decision back.</summary>
    public const int MaxListens = 2;

    private readonly IReadOnlyList<ICaptainsQuestionStore> _stores;
    private readonly IReadOnlyDictionary<string, (ICaptainsQuestionStore Store, CaptainsQuestionPhrases Table)> _questions;
    private readonly IMicOwnership _mic;
    private readonly ISpeechArbiter _arbiter;
    private readonly IOptionsMonitor<GsxOptions> _options;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<CaptainsQuestionDialogue> _logger;
    private int _running;

    public CaptainsQuestionDialogue(
        IEnumerable<ICaptainsQuestionStore> stores,
        IEnumerable<CaptainsQuestionPhrases> tables,
        IMicOwnership mic,
        ISpeechArbiter arbiter,
        IOptionsMonitor<GsxOptions> options,
        JsonlEventLog eventLog,
        ILogger<CaptainsQuestionDialogue> logger)
    {
        ArgumentNullException.ThrowIfNull(stores);
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(mic);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);
        _mic = mic;
        _arbiter = arbiter;
        _options = options;
        _eventLog = eventLog;
        _logger = logger;
        _stores = [.. stores];

        // Two tables for one topic is a wiring bug worth failing loudly on, not a
        // last-writer-wins.
        var tableByTopic = tables.ToDictionary(t => t.Topic, StringComparer.Ordinal);
        var questions = new Dictionary<string, (ICaptainsQuestionStore, CaptainsQuestionPhrases)>(StringComparer.Ordinal);
        foreach (var store in _stores)
        {
            if (tableByTopic.TryGetValue(store.Definition.Topic, out var table))
            {
                questions[store.Definition.Topic] = (store, table);
            }
        }

        _questions = questions;
    }

    public void Start()
    {
        foreach (var store in _stores)
        {
            if (_questions.ContainsKey(store.Definition.Topic))
            {
                store.QuestionRaised += OnQuestionRaised;
            }
            else
            {
                _logger.LogInformation(
                    "Captain's question {Topic} has no phrase table — answered on the web and by command only",
                    store.Definition.Topic);
            }
        }
    }

    public void Dispose()
    {
        foreach (var store in _stores)
        {
            store.QuestionRaised -= OnQuestionRaised;
        }
    }

    private void OnQuestionRaised(object? sender, CaptainsQuestion question) => _ = RunAsync(question);

    /// <summary>Voices one question; public so tests can drive it directly.</summary>
    public async Task RunAsync(CaptainsQuestion question, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(question);
        if (!_questions.TryGetValue(question.Topic, out var entry))
        {
            _logger.LogDebug("Captain's question {Topic} raised with no phrase table — not voiced", question.Topic);
            return;
        }

        await RunAsync(entry.Store, entry.Table, question, cancellationToken).ConfigureAwait(false);
    }

    private async Task RunAsync(ICaptainsQuestionStore store, CaptainsQuestionPhrases table, CaptainsQuestion question, CancellationToken cancellationToken)
    {
        if (!_options.CurrentValue.VoiceControlEnabled)
        {
            return; // the web card's buttons, the commands and the TTL answer it instead
        }

        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            _logger.LogDebug("{Question} skipped — another captain's question is being voiced", table.LogName);
            return;
        }

        try
        {
            IDisposable scope;
            try
            {
                scope = _mic.Borrow(table.MicOwner);
            }
            catch (InvalidOperationException)
            {
                _logger.LogDebug("{Question} skipped — microphone already borrowed", table.LogName);
                return;
            }

            using (scope)
            {
                _eventLog.Record(table.EventName, table.RaisedPayload(question));
                await _arbiter.SpeakAsync(table.SpokenPrompt(question), SpeechPriority.High, cancellationToken).ConfigureAwait(false);
                var grammar = table.Grammar(question);
                var repeat = table.RepeatPrompt ?? table.SpokenPrompt;

                var handedBack = false;
                for (var listen = 0; listen < MaxListens && !handedBack; listen++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!store.Asking.QuestionStands)
                    {
                        return; // answered elsewhere (web button, command, direct call, GSX settled it)
                    }

                    var heard = await _mic.ListenAsync(grammar, ListenTimeout, cancellationToken).ConfigureAwait(false);
                    if (heard is null)
                    {
                        continue;
                    }

                    var text = CommandMatcher.Normalize(heard);
                    if (ConfirmVocabulary.SayAgain.Any(s => text.Contains(s, StringComparison.Ordinal)))
                    {
                        await _arbiter.SpeakAsync(repeat(question), SpeechPriority.High, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    var reply = table.Interpret(heard, question);
                    switch (reply?.Kind)
                    {
                        case QuestionReplyKind.Yes:
                            Record(table, table.AnsweredPayload(question, reply, heard));
                            store.Accept("voice");
                            await ReadBackAsync(table.YesReadback, cancellationToken).ConfigureAwait(false);
                            return;

                        case QuestionReplyKind.No:
                            Record(table, table.AnsweredPayload(question, reply, heard));
                            store.Decline("voice", $"captain said '{heard}'");
                            await ReadBackAsync(table.NoReadback, cancellationToken).ConfigureAwait(false);
                            return;

                        case QuestionReplyKind.Pick:
                            if (store.Choose(reply.Choice!.Label, "voice"))
                            {
                                Record(table, table.AnsweredPayload(question, reply, heard));
                                await ReadBackAsync(table.PickReadback?.Invoke(reply.Choice), cancellationToken).ConfigureAwait(false);
                            }

                            return;

                        case QuestionReplyKind.Settled:
                            reply.Apply?.Invoke();
                            Record(table, table.AnsweredPayload(question, reply, heard));
                            return;

                        case QuestionReplyKind.HandBack:
                            handedBack = true;
                            break;

                        default:
                            await _arbiter.SpeakAsync(table.UnclearPrompt(question), SpeechPriority.High, cancellationToken).ConfigureAwait(false);
                            break;
                    }
                }

                // Nobody answered (or the captain handed it back): the question stays open on
                // the store — only the TTL, a button, a command or GSX's own menu settle it
                // (#166). The FO says so once and gives the mic back.
                var asking = store.Asking;
                _eventLog.Record(table.EventName, table.UnansweredPayload(question, asking));
                await _arbiter.SpeakAsync(table.UnansweredPhrase(question, asking), SpeechPriority.Normal, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Question} dialogue failed", table.LogName);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private void Record(CaptainsQuestionPhrases table, object? payload)
    {
        if (payload is not null)
        {
            _eventLog.Record(table.EventName, payload);
        }
    }

    private Task ReadBackAsync(string? text, CancellationToken cancellationToken)
        => text is null ? Task.CompletedTask : _arbiter.SpeakAsync(text, SpeechPriority.High, cancellationToken);
}
