namespace ProsimCompanion.Speech.Arbiter;

/// <summary>One queued utterance, tracked from enqueue to its terminal outcome.</summary>
public sealed class ArbiterItem
{
    internal ArbiterItem(SpeechRequest request, DateTimeOffset enqueuedAtUtc, CancellationToken callerToken)
    {
        Request = request;
        EnqueuedAtUtc = enqueuedAtUtc;
        CallerToken = callerToken;
    }

    public SpeechRequest Request { get; }
    public DateTimeOffset EnqueuedAtUtc { get; }

    /// <summary>The submitting caller's token — checked at dequeue and while deferred, and
    /// linked into the render so a caller cancel stops this item's own playback (never
    /// anyone else's).</summary>
    public CancellationToken CallerToken { get; }

    /// <summary>Resolved exactly once by the shell with the item's terminal outcome. The core
    /// never touches it (keeps the core pure/testable).</summary>
    public TaskCompletionSource<SpeechOutcome> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal bool IsExpired(DateTimeOffset nowUtc)
        => Request.Ttl is { } ttl && nowUtc - EnqueuedAtUtc > ttl;

    internal bool IsInvalid()
    {
        try
        {
            return Request.IsStillValid?.Invoke() == false;
        }
        catch
        {
            return false; // A throwing predicate never kills speech.
        }
    }
}

/// <summary>An item the core disposed of during a dequeue pass, for the shell to resolve and
/// publish. A null <paramref name="Outcome"/> means the event is informational only (Deferred,
/// Requeued) — the item is still pending and its caller task must NOT be resolved.</summary>
public sealed record ArbiterDisposal(
    ArbiterItem Item,
    SpeechOutcome? Outcome,
    SpeechEventKind Kind,
    string? Reason);

/// <summary>Result of one dequeue pass: the item to render (null when nothing is ready) plus
/// everything disposed of on the way (expired, suppressed, readmitted casualties…).</summary>
public sealed record ArbiterTakeResult(ArbiterItem? Next, IReadOnlyList<ArbiterDisposal> Disposals);

/// <summary>What became of a cancelled render.</summary>
public enum CancelDisposition
{
    /// <summary>Pre-empted Normal, still valid — put back at the HEAD of its queue to restart
    /// from the beginning right after the Critical (TTS renders whole clips; restart is the
    /// faithful equivalent of "resume").</summary>
    Restarted,

    /// <summary>Pre-empted and not eligible for restart (High/Low, or Normal gone invalid).</summary>
    Superseded,

    /// <summary>Cancelled for a non-pre-emption reason (shutdown or the caller's token).</summary>
    Cancelled,
}

/// <summary>
/// The pure state machine behind the speech arbiter — semantics carried verbatim from
/// Prosim2FO's proven arbiter: four strict-priority FIFO queues; Critical (only) pre-empts
/// in-flight playback; suppression rules evaluated at DEQUEUE (any Suppress wins, else any
/// Defer, else Allow; a throwing rule is ignored); deferred items readmitted to the TAIL of
/// their queue once rules relent, with TTL/validity re-checked; a pre-empted Normal restarts
/// from the head, High/Low are superseded. There is no dedup — callers own their latches.
///
/// No timers, threads, or audio here; the <see cref="SpeechArbiterService"/> shell owns those
/// and serializes access (repo convention: ProcessTick-style cores). Not thread-safe.
/// </summary>
public sealed class SpeechArbiterCore
{
    private const int PriorityCount = 4;

    private readonly IReadOnlyList<ISpeechSuppressionRule> _rules;
    private readonly List<ArbiterItem>[] _queues;
    private readonly List<ArbiterItem> _deferred = [];

    private ArbiterItem? _current;
    private bool _currentPreempted;

    public SpeechArbiterCore(IEnumerable<ISpeechSuppressionRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _rules = [.. rules];
        _queues = new List<ArbiterItem>[PriorityCount];
        for (var i = 0; i < PriorityCount; i++)
        {
            _queues[i] = [];
        }
    }

    /// <summary>Items sitting in the priority queues (excludes deferred and in-render).</summary>
    public int QueueDepth => _queues.Sum(q => q.Count);

    /// <summary>Items held back by a Defer verdict, in submission order.</summary>
    public int DeferredCount => _deferred.Count;

    /// <summary>The item currently rendering, if any.</summary>
    public ArbiterItem? Current => _current;

    /// <summary>
    /// Queues a request. <paramref name="preemptCurrent"/> is true when the in-flight render
    /// must be cancelled right now: the new item is Critical, something below Critical is
    /// rendering, and pre-emption is not already armed (two back-to-back Criticals must not
    /// double-cancel).
    /// </summary>
    public ArbiterItem Enqueue(
        SpeechRequest request,
        DateTimeOffset nowUtc,
        CancellationToken callerToken,
        out bool preemptCurrent)
    {
        ArgumentNullException.ThrowIfNull(request);

        var item = new ArbiterItem(request, nowUtc, callerToken);
        _queues[(int)request.Priority].Add(item);

        preemptCurrent = request.Priority == SpeechPriority.Critical
            && _current is not null
            && _current.Request.Priority < SpeechPriority.Critical
            && !_currentPreempted;
        if (preemptCurrent)
        {
            _currentPreempted = true;
        }

        return item;
    }

    /// <summary>
    /// One dequeue pass: readmits deferred items whose suppression lifted (to the tail of
    /// their queue), then takes the highest-priority FIFO item that survives the predecessor's
    /// check order — TTL, suppression, validity, caller cancel.
    /// </summary>
    public ArbiterTakeResult TakeNext(SpeechContext context, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(context);

        var disposals = new List<ArbiterDisposal>();
        ReadmitDeferred(context, nowUtc, disposals);

        while (true)
        {
            var item = DequeueHighest();
            if (item is null)
            {
                return new ArbiterTakeResult(null, disposals);
            }

            if (item.IsExpired(nowUtc))
            {
                disposals.Add(new ArbiterDisposal(item, SpeechOutcome.Expired, SpeechEventKind.Expired, "ttl elapsed"));
                continue;
            }

            var (verdict, rule) = Judge(item.Request, context);
            if (verdict == SpeechSuppressionVerdict.Suppress)
            {
                disposals.Add(new ArbiterDisposal(item, SpeechOutcome.Suppressed, SpeechEventKind.Suppressed, rule));
                continue;
            }

            if (verdict == SpeechSuppressionVerdict.Defer)
            {
                _deferred.Add(item);
                disposals.Add(new ArbiterDisposal(item, Outcome: null, SpeechEventKind.Deferred, rule));
                continue;
            }

            if (item.IsInvalid())
            {
                disposals.Add(new ArbiterDisposal(item, SpeechOutcome.Dropped, SpeechEventKind.Dropped, "no longer valid"));
                continue;
            }

            if (item.CallerToken.IsCancellationRequested)
            {
                disposals.Add(new ArbiterDisposal(item, SpeechOutcome.Dropped, SpeechEventKind.Dropped, "caller cancelled"));
                continue;
            }

            return new ArbiterTakeResult(item, disposals);
        }
    }

    /// <summary>Marks the item as rendering and closes the race where a Critical arrived
    /// between dequeue and render start: returns true when this sub-Critical item is already
    /// pre-empted before its synth begins (skip straight to the cancellation path rather than
    /// starting a synth that would immediately be abandoned).</summary>
    public bool BeginRender(ArbiterItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        _current = item;
        _currentPreempted = false;

        if (item.Request.Priority < SpeechPriority.Critical
            && _queues[(int)SpeechPriority.Critical].Count > 0)
        {
            _currentPreempted = true;
        }

        return _currentPreempted;
    }

    /// <summary>Resolves what a cancelled render becomes. A pre-empted Normal that is still
    /// valid goes back to the HEAD of the Normal queue (restart after the Critical).</summary>
    public CancelDisposition HandleCancelled(ArbiterItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var preempted = _currentPreempted;
        ClearCurrent();

        if (!preempted)
        {
            return CancelDisposition.Cancelled;
        }

        if (item.Request.Priority == SpeechPriority.Normal
            && !item.IsInvalid()
            && !item.CallerToken.IsCancellationRequested)
        {
            _queues[(int)SpeechPriority.Normal].Insert(0, item);
            return CancelDisposition.Restarted;
        }

        return CancelDisposition.Superseded;
    }

    /// <summary>Clears the in-render slot after a completed (or failed) render.</summary>
    public void ClearCurrent()
    {
        _current = null;
        _currentPreempted = false;
    }

    /// <summary>Empties every queue and the deferred list, returning the drained items for
    /// the shell to resolve as Dropped (shutdown).</summary>
    public IReadOnlyList<ArbiterItem> Drain()
    {
        var drained = new List<ArbiterItem>();
        foreach (var queue in _queues)
        {
            drained.AddRange(queue);
            queue.Clear();
        }

        drained.AddRange(_deferred);
        _deferred.Clear();
        return drained;
    }

    private void ReadmitDeferred(SpeechContext context, DateTimeOffset nowUtc, List<ArbiterDisposal> disposals)
    {
        for (var i = 0; i < _deferred.Count;)
        {
            var item = _deferred[i];
            if (item.IsExpired(nowUtc))
            {
                _deferred.RemoveAt(i);
                disposals.Add(new ArbiterDisposal(item, SpeechOutcome.Expired, SpeechEventKind.Expired, "expired while deferred"));
                continue;
            }

            if (item.IsInvalid())
            {
                _deferred.RemoveAt(i);
                disposals.Add(new ArbiterDisposal(item, SpeechOutcome.Dropped, SpeechEventKind.Dropped, "no longer valid while deferred"));
                continue;
            }

            if (item.CallerToken.IsCancellationRequested)
            {
                _deferred.RemoveAt(i);
                disposals.Add(new ArbiterDisposal(item, SpeechOutcome.Dropped, SpeechEventKind.Dropped, "caller cancelled"));
                continue;
            }

            var (verdict, rule) = Judge(item.Request, context);
            if (verdict == SpeechSuppressionVerdict.Suppress)
            {
                _deferred.RemoveAt(i);
                disposals.Add(new ArbiterDisposal(item, SpeechOutcome.Suppressed, SpeechEventKind.Suppressed, $"suppressed while deferred ({rule})"));
                continue;
            }

            if (verdict == SpeechSuppressionVerdict.Allow)
            {
                // Tail, not head: items enqueued while this one was held legitimately play
                // first (predecessor semantics — readmit uses AddLast, restart uses AddFirst).
                _deferred.RemoveAt(i);
                _queues[(int)item.Request.Priority].Add(item);
                disposals.Add(new ArbiterDisposal(item, Outcome: null, SpeechEventKind.Requeued, "readmitted after deferral"));
                continue;
            }

            i++; // Still deferred.
        }
    }

    private ArbiterItem? DequeueHighest()
    {
        for (var p = PriorityCount - 1; p >= 0; p--)
        {
            var queue = _queues[p];
            if (queue.Count > 0)
            {
                var item = queue[0];
                queue.RemoveAt(0);
                return item;
            }
        }

        return null;
    }

    private (SpeechSuppressionVerdict Verdict, string? RuleName) Judge(
        SpeechRequest request, SpeechContext context)
    {
        var combined = SpeechSuppressionVerdict.Allow;
        string? ruleName = null;
        foreach (var rule in _rules)
        {
            SpeechSuppressionVerdict verdict;
            try
            {
                verdict = rule.Evaluate(request, context);
            }
            catch
            {
                continue; // A throwing rule is ignored for this evaluation.
            }

            if (verdict == SpeechSuppressionVerdict.Suppress)
            {
                return (SpeechSuppressionVerdict.Suppress, rule.Name); // Suppress short-circuits.
            }

            if (verdict == SpeechSuppressionVerdict.Defer && combined == SpeechSuppressionVerdict.Allow)
            {
                combined = SpeechSuppressionVerdict.Defer;
                ruleName = rule.Name;
            }
        }

        return (combined, ruleName);
    }
}
