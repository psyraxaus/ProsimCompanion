using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Speech;
using ProsimCompanion.Speech.Arbiter;

namespace ProsimCompanion.Speech.Llm;

/// <summary>Everything one streamed narration needs.</summary>
/// <param name="Kind">Name for the log and the session event ("briefing.departure", "debrief").</param>
/// <param name="System">The system prompt.</param>
/// <param name="User">The user prompt (the fact block and the instruction).</param>
/// <param name="Allowed">Every number the narration may speak.</param>
/// <param name="Sections">The deterministic template, clause by clause — the fallback.</param>
/// <param name="Header">Priority, tag, TTL, validity and role of the spoken item; its
/// <c>Text</c> is the item's label for logs.</param>
/// <param name="Epilogue">Deterministic sentences always spoken last, whatever the model did
/// (the debrief's day and logbook lines — never to be paraphrased).</param>
public sealed record NarrationPlan(
    string Kind,
    string System,
    string User,
    IReadOnlyList<double> Allowed,
    IReadOnlyList<NarrationSection> Sections,
    SpeechRequest Header,
    IReadOnlyList<string> Epilogue)
{
    /// <summary>Called once, when the first verified model sentence is handed to the arbiter
    /// (issue #149: the FO questions cancel their "stand by" timer and their time budget here).
    /// Not called when the template speaks instead.</summary>
    public Action? OnFirstSpeech { get; init; }
}

/// <summary>What a streamed narration came to.</summary>
/// <param name="Text">The text actually spoken (what reached the arbiter and was played).</param>
/// <param name="Outcome">The spoken item's fate.</param>
/// <param name="LlmSentences">Model sentences released for speech.</param>
/// <param name="Takeover">Why the template finished or replaced the model's text; None = it did not.</param>
/// <param name="TemplateSections">Template sections spoken because of a takeover.</param>
/// <param name="Preempted">The arbiter cut the item short (a Critical, expiry, a cancel).</param>
/// <param name="FirstTokenMs">Request sent → first text delta.</param>
/// <param name="FirstAudioMs">Request sent → first sentence starts playing.</param>
public sealed record NarrationResult(
    string Text,
    SpeechOutcome Outcome,
    int LlmSentences,
    TakeoverReason Takeover,
    int TemplateSections,
    bool Preempted,
    double? FirstTokenMs,
    double? FirstAudioMs);

/// <summary>
/// Speaks an LLM narration while it is still being written (issue #147). Deltas from
/// <see cref="OpenAiChatClient.StreamAsync"/> are cut into sentences
/// (<see cref="SentenceChunker"/>); each sentence is verified (<see cref="NarrationCore"/>)
/// and only then handed to the arbiter as the next segment of ONE streamed item
/// (<see cref="StreamedUtterance"/>) — so the first words are heard after the first sentence, not
/// after the whole completion.
/// <para>
/// The template is the floor at every point. Nothing verified before the model fails, times
/// out or says nothing → the whole template is spoken as an ordinary single utterance.
/// A failure after speech began → the template's not-yet-heard sections are appended to the
/// same item, so the pilot hears one continuous narration and never a restart. The same
/// happens when the speech runs dry and no new sentence arrives within
/// <see cref="StallAfter"/>. There is no strict re-ask on this path: it would cost the
/// latency streaming exists to remove.
/// </para>
/// An item the arbiter ends early (a Critical cut it) is over: the model request is cancelled
/// and nothing more is said.
/// </summary>
public sealed class StreamingNarrator
{
    private readonly OpenAiChatClient _llm;
    private readonly ISpeechArbiter _arbiter;
    private readonly JsonlEventLog _eventLog;
    private readonly IOptionsMonitor<BriefingOptions> _options;
    private readonly ILogger<StreamingNarrator> _logger;

    public StreamingNarrator(
        OpenAiChatClient llm,
        ISpeechArbiter arbiter,
        JsonlEventLog eventLog,
        IOptionsMonitor<BriefingOptions> options,
        ILogger<StreamingNarrator> logger)
    {
        ArgumentNullException.ThrowIfNull(llm);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _llm = llm;
        _arbiter = arbiter;
        _eventLog = eventLog;
        _options = options;
        _logger = logger;
    }

    /// <summary>Session event type carrying the figures the feature is judged by.</summary>
    public const string EventType = "llm.stream";

    /// <summary>How long the speech may wait, with nothing left to say, for the model's next
    /// sentence before the template takes over. Internal so tests can shorten it.</summary>
    internal TimeSpan StallAfter { get; init; } = TimeSpan.FromSeconds(4);

    /// <summary>How often the wait for the next delta looks at the speech side.</summary>
    internal TimeSpan WatchInterval { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Runs one narration to the end of its speech. Never throws for a model or
    /// speech failure — the template covers those; only the caller's own cancel throws.</summary>
    public async Task<NarrationResult> RunAsync(NarrationPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var clock = Stopwatch.StartNew();
        var core = new NarrationCore(plan.Allowed, plan.Sections);
        var chunker = new SentenceChunker();
        StreamedUtterance? stream = null;
        Task<SpeechOutcome>? speaking = null;
        double? firstTokenMs = null;
        var takeover = TakeoverReason.None;
        var preempted = false;

        try
        {
            using var llmCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            // Releases one verified model sentence; the first one opens the spoken item.
            void Release(string sentence)
            {
                if (stream is null)
                {
                    stream = new StreamedUtterance();
                    stream.TryWrite(new SpeechSegment(sentence, Cacheable: false));
                    speaking = _arbiter.EnqueueAsync(plan.Header with { Stream = stream }, cancellationToken);
                    plan.OnFirstSpeech?.Invoke();
                }
                else
                {
                    stream.TryWrite(new SpeechSegment(sentence, Cacheable: false));
                }
            }

            bool Offer(string sentence)
            {
                if (core.Accept(sentence))
                {
                    Release(sentence);
                    return true;
                }

                _logger.LogWarning(
                    "{Kind}: a streamed sentence failed number verification (unverified: {Tokens}) — the template takes over",
                    plan.Kind, string.Join(", ", core.LastOffending));
                return false;
            }

            var deltas = _llm.StreamAsync(plan.System, plan.User, llmCts.Token).GetAsyncEnumerator(llmCts.Token);
            Task<bool>? next = null;
            try
            {
                while (takeover == TakeoverReason.None && !preempted)
                {
                    next ??= deltas.MoveNextAsync().AsTask();
                    var first = await Task.WhenAny(next, Task.Delay(WatchInterval, cancellationToken)).ConfigureAwait(false);
                    if (first != next)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (stream is { } open)
                        {
                            if (open.Aborted.IsCancellationRequested)
                            {
                                preempted = true;
                            }
                            else if (open.StarvedFor(DateTimeOffset.UtcNow) >= StallAfter)
                            {
                                takeover = TakeoverReason.Stalled;
                            }
                        }

                        continue;
                    }

                    var more = await next.ConfigureAwait(false);
                    next = null;
                    if (!more)
                    {
                        // The model finished: what is left in the chunker is its last sentence.
                        if (chunker.Flush() is { } tail && !Offer(tail))
                        {
                            takeover = TakeoverReason.VerifyFailed;
                        }

                        break;
                    }

                    firstTokenMs ??= clock.Elapsed.TotalMilliseconds;
                    foreach (var sentence in chunker.Push(deltas.Current))
                    {
                        if (!Offer(sentence))
                        {
                            takeover = TakeoverReason.VerifyFailed;
                            break;
                        }
                    }
                }
            }
            catch (TimeoutException ex)
            {
                next = next is { IsCompleted: true } ? null : next;
                takeover = TakeoverReason.Timeout;
                _logger.LogWarning("{Kind}: {Reason} — the template takes over", plan.Kind, ex.Message);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                next = next is { IsCompleted: true } ? null : next;
                takeover = TakeoverReason.Error;
                _logger.LogWarning(ex, "{Kind}: the LLM stream failed — the template takes over", plan.Kind);
            }
            finally
            {
                // Stop the model, let an in-flight read finish, then release the response.
                await llmCts.CancelAsync().ConfigureAwait(false);
                if (next is not null)
                {
                    try
                    {
                        await next.ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // The read was cancelled or broke: either way it is over.
                    }
                }

                try
                {
                    await deltas.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "{Kind}: disposing the LLM stream failed", plan.Kind);
                }
            }

            preempted |= stream is { } current && current.Aborted.IsCancellationRequested;
            if (takeover == TakeoverReason.None && !preempted && core.Spoken.Count == 0)
            {
                // The model answered and said nothing usable.
                takeover = TakeoverReason.Error;
                _logger.LogWarning("{Kind}: the LLM returned no text — using the template", plan.Kind);
            }

            var templateSections = 0;
            string? wholeText = null;
            if (!preempted)
            {
                if (stream is null)
                {
                    // Nothing was said yet: the plain template, as ONE ordinary utterance
                    // (cached and prewarmed like every non-LLM narration).
                    templateSections = plan.Sections.Count;
                    wholeText = string.Join(" ", plan.Sections.Select(s => s.Text).Concat(plan.Epilogue));
                    if (wholeText.Length > 0)
                    {
                        speaking = _arbiter.EnqueueAsync(plan.Header with { Text = wholeText }, cancellationToken);
                    }
                    // A plan with no template (the FO questions, #149) says nothing here: the
                    // caller decides what a silent model means.
                }
                else
                {
                    if (takeover != TakeoverReason.None)
                    {
                        foreach (var section in core.Remaining())
                        {
                            if (stream.TryWrite(new SpeechSegment(section.Text)))
                            {
                                templateSections++;
                            }
                        }
                    }

                    foreach (var line in plan.Epilogue)
                    {
                        stream.TryWrite(new SpeechSegment(line));
                    }
                }
            }

            stream?.Complete();
            var outcome = speaking is null ? SpeechOutcome.Dropped : await speaking.ConfigureAwait(false);
            preempted |= stream is { } ended && ended.Aborted.IsCancellationRequested;

            double? firstAudioMs = null;
            if (stream is { FirstAudio.IsCompletedSuccessfully: true })
            {
                // The stopwatch and the stream's clock started together for all practical
                // purposes; the wall-clock difference is what the pilot waited.
                firstAudioMs = Math.Max(0, clock.Elapsed.TotalMilliseconds
                    - (DateTimeOffset.UtcNow - stream.FirstAudio.Result).TotalMilliseconds);
            }

            var text = stream is null ? wholeText ?? "" : string.Join(" ", stream.Spoken);
            var result = new NarrationResult(
                text, outcome, core.Spoken.Count, takeover, templateSections, preempted, firstTokenMs, firstAudioMs);
            Record(plan, result);
            return result;
        }
        finally
        {
            stream?.Dispose();
        }
    }

    private void Record(NarrationPlan plan, NarrationResult result)
    {
        _logger.LogInformation(
            "{Kind} streamed: first token {FirstTokenMs:F0} ms, first audio {FirstAudioMs:F0} ms, {Sentences} LLM sentence(s), "
            + "template took over: {Takeover} ({TemplateSections} section(s)), pre-empted: {Preempted}, outcome {Outcome}",
            plan.Kind, result.FirstTokenMs, result.FirstAudioMs, result.LlmSentences,
            result.Takeover, result.TemplateSections, result.Preempted, result.Outcome);
        _eventLog.Record(EventType, new
        {
            kind = plan.Kind,
            firstTokenMs = Round(result.FirstTokenMs),
            firstAudioMs = Round(result.FirstAudioMs),
            sentencesSpoken = result.LlmSentences,
            templateTookOver = result.Takeover != TakeoverReason.None,
            takeoverReason = result.Takeover switch
            {
                TakeoverReason.VerifyFailed => "verify-failed",
                TakeoverReason.Stalled => "stalled",
                TakeoverReason.Timeout => "timeout",
                TakeoverReason.Error => "error",
                _ => "none",
            },
            sectionsFromTemplate = result.TemplateSections,
            preempted = result.Preempted,
            outcome = result.Outcome.ToString(),
            model = _options.CurrentValue.LlmModel,
        });
    }

    private static double? Round(double? value) => value is { } v ? Math.Round(v) : null;
}
