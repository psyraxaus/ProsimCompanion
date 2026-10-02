using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Playback;
using ProsimCompanion.Speech.Tts;

namespace ProsimCompanion.Speech.Arbiter;

/// <summary>
/// The async shell around <see cref="SpeechArbiterCore"/>: one dedicated pump task renders
/// utterances end-to-end (synthesize → play), a semaphore wakes it on enqueue, and a 500 ms
/// poll re-judges deferred items so speech resumes promptly when sterile ends. All core access
/// is serialized under one lock; Critical pre-emption cancels the in-flight render's token.
/// Nothing here throws to callers or stalls the queue.
/// </summary>
public sealed class SpeechArbiterService : ISpeechArbiter, ISpeechControl, IDisposable
{
    private static readonly TimeSpan DeferPoll = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan DisposeWait = TimeSpan.FromSeconds(2);

    /// <summary>How often a streamed item that is waiting for its next segment looks for a
    /// High callout to speak in the gap (issue #147).</summary>
    private static readonly TimeSpan StreamPoll = TimeSpan.FromMilliseconds(150);
    private const int RecentUtterancesKept = 30;

    private readonly IOptionsMonitor<SpeechOptions> _options;
    private readonly IOptionsMonitor<VoicesOptions> _voices;
    private readonly TtsRouter _router;
    private readonly ISpeechPlayback _playback;
    private readonly IFlightPhaseSource _flight;
    private readonly SpeechStatusStore _store;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<SpeechArbiterService> _logger;
    private readonly Crew.AccentVoiceResolver? _accents;

    private readonly SpeechArbiterCore _core;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _pump;

    private CancellationTokenSource? _renderCts;
    private int _disposed;

    // Pump-thread only (RenderOneAsync is serialized): which roles have already logged their
    // blank-voice fallback, so the log line appears once per role, not per utterance.
    private readonly HashSet<SpeechRole> _roleFallbackLogged = [];

    public SpeechArbiterService(
        IOptionsMonitor<SpeechOptions> options,
        IOptionsMonitor<VoicesOptions> voices,
        TtsRouter router,
        ISpeechPlayback playback,
        IFlightPhaseSource flight,
        SpeechStatusStore store,
        JsonlEventLog eventLog,
        ILogger<SpeechArbiterService> logger,
        Persona.QuietState quietState,
        Crew.AccentVoiceResolver? accents = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(voices);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(playback);
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(quietState);

        _options = options;
        _voices = voices;
        _router = router;
        _playback = playback;
        _flight = flight;
        _store = store;
        _eventLog = eventLog;
        _logger = logger;
        _accents = accents;

        _core = new SpeechArbiterCore(
        [
            new SterileCockpitRule(() => options.CurrentValue),
            // "quiet please": drops the Low band while the session latch is engaged.
            new Persona.QuietCockpitRule(quietState),
        ]);
        _store.Update(s => s with { Enabled = options.CurrentValue.Enabled });
        _pump = Task.Run(PumpAsync);
    }

    public event Action<SpeechArbiterEvent>? Observed;

    public Task<SpeechOutcome> EnqueueAsync(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Text))
        {
            return Task.FromResult(SpeechOutcome.Dropped);
        }

        if (_disposed == 1 || !_options.CurrentValue.Enabled)
        {
            Raise(SpeechEventKind.Dropped, request.Priority, request.Tag, request.Text,
                _disposed == 1 ? "shutdown" : "speech pillar disabled");
            return Task.FromResult(SpeechOutcome.Dropped);
        }

        ArbiterItem item;
        bool preempt;
        lock (_gate)
        {
            // Re-checked under the lock: Dispose drains the core under the same lock after
            // setting the flag, so an item admitted here is either processed or drained —
            // never stranded with an unresolved Completion.
            if (_disposed == 1)
            {
                Raise(SpeechEventKind.Dropped, request.Priority, request.Tag, request.Text, "shutdown");
                return Task.FromResult(SpeechOutcome.Dropped);
            }

            item = _core.Enqueue(request, DateTimeOffset.UtcNow, cancellationToken, out preempt);
            if (preempt)
            {
                _renderCts?.Cancel();
            }
        }

        Raise(SpeechEventKind.Enqueued, request.Priority, request.Tag, request.Text, null);
        try
        {
            _signal.Release();
        }
        catch (ObjectDisposedException)
        {
            // Shutdown raced the enqueue; the item was drained and resolved by Dispose.
        }
        PublishStatus();
        return item.Completion.Task;
    }

    public Task<SpeechOutcome> SpeakAsync(
        string text,
        SpeechPriority priority = SpeechPriority.Normal,
        CancellationToken cancellationToken = default)
        => EnqueueAsync(new SpeechRequest(text, priority), cancellationToken);

    /// <inheritdoc/>
    public void SpeakTest(string text)
        => _ = EnqueueAsync(new SpeechRequest(text, SpeechPriority.Normal, Tag: "test"));

    public void Dispose()
    {
        // Idempotent: shutdown may dispose explicitly (to silence audio early) and the host
        // container disposes again afterwards.
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _shutdown.Cancel();
        lock (_gate)
        {
            _renderCts?.Cancel();
        }

        _signal.Release();
        try
        {
            _pump.Wait(DisposeWait);
        }
        catch (AggregateException)
        {
            // Pump exceptions were already logged inside.
        }

        IReadOnlyList<ArbiterItem> drained;
        lock (_gate)
        {
            drained = _core.Drain();
        }

        foreach (var item in drained)
        {
            item.Request.Stream?.Close(aborted: true);
            item.Completion.TrySetResult(SpeechOutcome.Dropped);
        }

        _shutdown.Dispose();
        _signal.Dispose();
    }

    private async Task PumpAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                TimeSpan wait;
                lock (_gate)
                {
                    wait = _core.DeferredCount > 0 ? DeferPoll : Timeout.InfiniteTimeSpan;
                }

                try
                {
                    await _signal.WaitAsync(wait, _shutdown.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                while (!_shutdown.IsCancellationRequested)
                {
                    ArbiterTakeResult take;
                    lock (_gate)
                    {
                        take = _core.TakeNext(BuildContext(), DateTimeOffset.UtcNow);
                    }

                    HandleDisposals(take.Disposals);
                    if (take.Next is null)
                    {
                        break;
                    }

                    await RenderOneAsync(take.Next).ConfigureAwait(false);
                }

                PublishStatus();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Speech pump crashed — speech is dead until restart");
        }
    }

    private async Task RenderOneAsync(ArbiterItem item)
    {
        var request = item.Request;
        CancellationTokenSource renderCts;
        lock (_gate)
        {
            var preemptedBeforeStart = _core.BeginRender(item);
            renderCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, item.CallerToken);
            _renderCts = renderCts;
            if (preemptedBeforeStart)
            {
                // A Critical arrived between dequeue and here — skip straight to the
                // cancellation path rather than starting a synth we'd immediately abandon.
                renderCts.Cancel();
            }
        }

        Raise(SpeechEventKind.Dequeued, request.Priority, request.Tag, request.Text, null);
        _store.Update(s => s with { NowPlaying = request.Text });

        SpeechOutcome? outcome;
        try
        {
            renderCts.Token.ThrowIfCancellationRequested();

            if (request.Stream is { } stream)
            {
                // A streamed item (issue #147): its segments are spoken one after another
                // inside this one slot, so nothing at its priority or below gets between them.
                var segments = await RenderStreamAsync(item, stream, renderCts.Token).ConfigureAwait(false);
                if (segments < 0)
                {
                    outcome = SpeechOutcome.Dropped;
                    Raise(SpeechEventKind.Dropped, request.Priority, request.Tag, request.Text, "no longer valid");
                }
                else
                {
                    outcome = SpeechOutcome.Spoken;
                    Raise(SpeechEventKind.Spoken, request.Priority, request.Tag, request.Text,
                        $"stream — {segments} segment(s)");
                }
            }
            else
            {
                var heard = await SynthesizeAndPlayAsync(
                    request, request.Text, cacheable: true, withChime: true, onAudioStart: null, renderCts.Token)
                    .ConfigureAwait(false);

                // Total synthesis failure means silence, not an error — the utterance is done
                // either way and the queue moves on (router already logged the loss).
                outcome = SpeechOutcome.Spoken;
                Raise(SpeechEventKind.Spoken, request.Priority, request.Tag, request.Text,
                    heard ? null : "silent — no TTS provider available");
            }
        }
        catch (OperationCanceledException)
        {
            CancelDisposition disposition;
            lock (_gate)
            {
                disposition = _core.HandleCancelled(item);
            }

            switch (disposition)
            {
                case CancelDisposition.Restarted:
                    Raise(SpeechEventKind.Preempted, request.Priority, request.Tag, request.Text, null);
                    Raise(SpeechEventKind.Requeued, request.Priority, request.Tag, request.Text,
                        "pre-empted (Normal) — restarting");
                    _signal.Release();
                    outcome = null; // Still pending — do not resolve the caller task.
                    break;

                case CancelDisposition.Superseded:
                    Raise(SpeechEventKind.Preempted, request.Priority, request.Tag, request.Text, null);
                    Raise(SpeechEventKind.Dropped, request.Priority, request.Tag, request.Text, "pre-empted");
                    outcome = SpeechOutcome.Superseded;
                    break;

                default:
                    Raise(SpeechEventKind.Dropped, request.Priority, request.Tag, request.Text, "cancelled");
                    outcome = SpeechOutcome.Dropped;
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Speech render failed");
            Raise(SpeechEventKind.Failed, request.Priority, request.Tag, request.Text, ex.Message);
            outcome = SpeechOutcome.Failed;
        }
        finally
        {
            lock (_gate)
            {
                _core.ClearCurrent();
                if (ReferenceEquals(_renderCts, renderCts))
                {
                    _renderCts = null;
                }
            }

            renderCts.Dispose();
        }

        // Before the caller's task resolves: a producer woken by the outcome must already
        // see whether its stream was cut short.
        request.Stream?.Close(aborted: outcome != SpeechOutcome.Spoken);

        if (outcome is { } terminal)
        {
            item.Completion.TrySetResult(terminal);
        }

        _store.Update(s => s with { NowPlaying = null });
    }

    /// <summary>Synthesizes and plays one text in the caller's slot. Returns false when no
    /// TTS provider produced audio (silence — not an error). <paramref name="cacheable"/>
    /// false keeps one-off text out of the TTS disk cache, read and write.</summary>
    private async Task<bool> SynthesizeAndPlayAsync(
        SpeechRequest request, string text, bool cacheable, bool withChime, Action? onAudioStart,
        CancellationToken token)
    {
        // Normalized BEFORE synthesis so the TTS cache key matches prewarmed phrases and
        // "FL350" is never read as "Florida 350".
        var spoken = Callouts.AviationSpeech.Normalize(text);

        // Speaker role → (voice, intercom decision). A role with no configured voice
        // falls back to the FO voice, announced once per role rather than per utterance.
        var roleVoice = RoleVoiceResolver.Resolve(request.Role, _voices.CurrentValue);
        if (roleVoice.FellBackToFoVoice && _roleFallbackLogged.Add(request.Role))
        {
            _logger.LogInformation(
                "No voice configured for role {Role} — using the First Officer voice",
                request.Role);
        }

        // Accent localization (issue #53): a per-provider selector so Google can carry a
        // Chirp locale voice while local providers keep a voice they actually have.
        var accentSelector = _accents?.ProviderVoiceSelector(request.Role);
        TtsAudio? audio;
        using (cacheable ? null : TtsDiskCache.Bypass())
        {
            audio = await _router
                .SynthesizeAsync(spoken, token, roleVoice.VoiceOverride, accentSelector)
                .ConfigureAwait(false);
        }

        if (audio is null)
        {
            return false;
        }

        // Chime then speech, back-to-back in this one slot — synthesized FIRST so the
        // chime never leads a synth stall, and skipped entirely when there is no voice
        // to follow it. A failed chime never blocks the utterance.
        if (withChime && !string.IsNullOrWhiteSpace(request.Chime))
        {
            try
            {
                await _playback.PlayChimeAsync(request.Chime!, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Chime {Chime} failed", request.Chime);
            }
        }

        onAudioStart?.Invoke();
        await _playback.PlayAsync(audio.WavBytes, token, roleVoice.IntercomOverride).ConfigureAwait(false);
        return true;
    }

    /// <summary>Speaks a streamed item's segments in order until the producer completes the
    /// stream. Returns the number spoken, or −1 when the item's validity predicate went false
    /// between segments. Cancellation (a Critical, shutdown, the caller) throws out to the
    /// normal cancellation path, where a stream always ends — it is never restarted.</summary>
    private async Task<int> RenderStreamAsync(ArbiterItem item, StreamedUtterance stream, CancellationToken token)
    {
        var request = item.Request;
        var spoken = 0;
        while (true)
        {
            // Owner decision D3: a High callout is spoken between two sentences (and while
            // the stream waits for its next one) instead of behind the whole narration.
            await RenderInterjectionsAsync(token).ConfigureAwait(false);

            if (!stream.Reader.TryRead(out var segment))
            {
                stream.MarkStarved(DateTimeOffset.UtcNow);
                using var poll = CancellationTokenSource.CreateLinkedTokenSource(token);
                poll.CancelAfter(StreamPoll);
                try
                {
                    if (!await stream.Reader.WaitToReadAsync(poll.Token).ConfigureAwait(false))
                    {
                        return spoken; // Completed and drained.
                    }
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    // Poll tick: look for a High callout, then wait again.
                }

                continue;
            }

            stream.MarkFed();
            token.ThrowIfCancellationRequested();
            if (item.IsInvalid())
            {
                return -1;
            }

            if (string.IsNullOrWhiteSpace(segment.Text))
            {
                continue;
            }

            _store.Update(s => s with { NowPlaying = segment.Text });
            var first = spoken == 0;
            await SynthesizeAndPlayAsync(
                request, segment.Text, segment.Cacheable, withChime: first,
                onAudioStart: first ? () => stream.MarkFirstAudio(DateTimeOffset.UtcNow) : null, token)
                .ConfigureAwait(false);
            spoken++;
            stream.MarkSpoken(segment.Text);
            Raise(SpeechEventKind.Segment, request.Priority, request.Tag, segment.Text,
                segment.Cacheable ? null : "llm");
        }
    }

    /// <summary>Speaks every queued High (or Critical) item, in priority order, inside the
    /// current streamed item's slot. If the stream itself is cut while one is playing, that
    /// callout goes with it and the cancellation propagates.</summary>
    private async Task RenderInterjectionsAsync(CancellationToken streamToken)
    {
        while (true)
        {
            ArbiterTakeResult take;
            lock (_gate)
            {
                // Checked under the lock: a Critical is enqueued and the stream is cancelled
                // in one locked step, so a Critical is never taken here as an "interjection"
                // (it would be cut with the stream it is pre-empting).
                streamToken.ThrowIfCancellationRequested();
                take = _core.TakeNext(BuildContext(), DateTimeOffset.UtcNow, SpeechPriority.High);
            }

            HandleDisposals(take.Disposals);
            if (take.Next is not { } item)
            {
                return;
            }

            var request = item.Request;
            Raise(SpeechEventKind.Dequeued, request.Priority, request.Tag, request.Text, "between stream segments");

            SpeechOutcome outcome;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(streamToken, item.CallerToken);
            try
            {
                if (request.Stream is not null)
                {
                    // A stream inside a stream is not supported; nothing produces one today.
                    request.Stream.Close(aborted: true);
                    outcome = SpeechOutcome.Dropped;
                    Raise(SpeechEventKind.Dropped, request.Priority, request.Tag, request.Text, "nested stream");
                }
                else
                {
                    var heard = await SynthesizeAndPlayAsync(
                        request, request.Text, cacheable: true, withChime: true, onAudioStart: null, cts.Token)
                        .ConfigureAwait(false);
                    outcome = SpeechOutcome.Spoken;
                    Raise(SpeechEventKind.Spoken, request.Priority, request.Tag, request.Text,
                        heard ? null : "silent — no TTS provider available");
                }
            }
            catch (OperationCanceledException) when (streamToken.IsCancellationRequested)
            {
                Raise(SpeechEventKind.Preempted, request.Priority, request.Tag, request.Text, null);
                Raise(SpeechEventKind.Dropped, request.Priority, request.Tag, request.Text, "pre-empted");
                item.Completion.TrySetResult(SpeechOutcome.Superseded);
                throw;
            }
            catch (OperationCanceledException)
            {
                outcome = SpeechOutcome.Dropped;
                Raise(SpeechEventKind.Dropped, request.Priority, request.Tag, request.Text, "cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Speech render failed");
                Raise(SpeechEventKind.Failed, request.Priority, request.Tag, request.Text, ex.Message);
                outcome = SpeechOutcome.Failed;
            }

            item.Completion.TrySetResult(outcome);
        }
    }

    private void HandleDisposals(IReadOnlyList<ArbiterDisposal> disposals)
    {
        foreach (var disposal in disposals)
        {
            var request = disposal.Item.Request;
            Raise(disposal.Kind, request.Priority, request.Tag, request.Text, disposal.Reason);
            if (disposal.Outcome is { } outcome)
            {
                // A streamed item that never got to speak: tell its producer to stop.
                request.Stream?.Close(aborted: true);
                disposal.Item.Completion.TrySetResult(outcome);
            }
        }
    }

    private SpeechContext BuildContext()
    {
        var snapshot = _flight.Snapshot().Data;
        return new SpeechContext(
            _flight.CurrentPhase,
            snapshot?.AltitudeFt ?? 0,
            snapshot?.IsValid ?? false);
    }

    private void Raise(SpeechEventKind kind, SpeechPriority priority, string? tag, string text, string? reason)
    {
        var evt = new SpeechArbiterEvent(kind, priority, tag, text, reason);
        _eventLog.Record("speech." + kind.ToString().ToLowerInvariant(), new
        {
            priority = priority.ToString(),
            tag,
            text,
            reason,
        });

        if (kind is SpeechEventKind.Spoken or SpeechEventKind.Segment or SpeechEventKind.Dropped or SpeechEventKind.Expired
            or SpeechEventKind.Suppressed or SpeechEventKind.Failed or SpeechEventKind.Deferred)
        {
            var view = new UtteranceView(
                DateTimeOffset.UtcNow, text, priority.ToString(), tag ?? "", kind.ToString());
            _store.Update(s => s with
            {
                RecentUtterances = [view, .. s.RecentUtterances.Take(RecentUtterancesKept - 1)],
            });
        }

        try
        {
            Observed?.Invoke(evt);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Speech observer threw");
        }
    }

    private void PublishStatus()
    {
        var options = _options.CurrentValue;
        int depth;
        lock (_gate)
        {
            depth = _core.QueueDepth + _core.DeferredCount;
        }

        var sterile = SterileCockpitRule.IsSterile(options, BuildContext());
        _store.Update(s => s with
        {
            Enabled = options.Enabled,
            QueueDepth = depth,
            SterileCockpit = sterile,
        });
    }
}
