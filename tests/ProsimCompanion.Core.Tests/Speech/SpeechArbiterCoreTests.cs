using ProsimCompanion.Speech.Arbiter;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

public sealed class SpeechArbiterCoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedRule : ISpeechSuppressionRule
    {
        public string Name => "fixed";
        public Func<SpeechRequest, SpeechSuppressionVerdict> Verdict { get; set; } =
            _ => SpeechSuppressionVerdict.Allow;

        public SpeechSuppressionVerdict Evaluate(SpeechRequest request, SpeechContext context)
            => Verdict(request);
    }

    private static SpeechArbiterCore Core(params ISpeechSuppressionRule[] rules) => new(rules);

    private static ArbiterItem Enqueue(
        SpeechArbiterCore core, string text, SpeechPriority priority,
        TimeSpan? ttl = null, Func<bool>? valid = null, CancellationToken ct = default)
        => core.Enqueue(new SpeechRequest(text, priority, ttl, valid), Now, ct, out _);

    [Fact]
    public void TakeNext_StrictPriority_FifoWithinBand()
    {
        var core = Core();
        Enqueue(core, "low", SpeechPriority.Low);
        Enqueue(core, "normal1", SpeechPriority.Normal);
        Enqueue(core, "high", SpeechPriority.High);
        Enqueue(core, "normal2", SpeechPriority.Normal);

        Assert.Equal("high", core.TakeNext(SpeechContext.Unknown, Now).Next?.Request.Text);
        Assert.Equal("normal1", core.TakeNext(SpeechContext.Unknown, Now).Next?.Request.Text);
        Assert.Equal("normal2", core.TakeNext(SpeechContext.Unknown, Now).Next?.Request.Text);
        Assert.Equal("low", core.TakeNext(SpeechContext.Unknown, Now).Next?.Request.Text);
        Assert.Null(core.TakeNext(SpeechContext.Unknown, Now).Next);
    }

    [Fact]
    public void Enqueue_CriticalWhileSubCriticalRenders_ArmsPreemptionOnce()
    {
        var core = Core();
        var normal = Enqueue(core, "normal", SpeechPriority.Normal);
        core.TakeNext(SpeechContext.Unknown, Now);
        core.BeginRender(normal);

        core.Enqueue(new SpeechRequest("crit1", SpeechPriority.Critical), Now, default, out var first);
        core.Enqueue(new SpeechRequest("crit2", SpeechPriority.Critical), Now, default, out var second);

        Assert.True(first);
        Assert.False(second); // Back-to-back Criticals must not double-cancel.
    }

    [Fact]
    public void Enqueue_CriticalWhileCriticalRenders_DoesNotPreempt()
    {
        var core = Core();
        var current = Enqueue(core, "crit", SpeechPriority.Critical);
        core.TakeNext(SpeechContext.Unknown, Now);
        core.BeginRender(current);

        core.Enqueue(new SpeechRequest("next", SpeechPriority.Critical), Now, default, out var preempt);

        Assert.False(preempt);
    }

    [Fact]
    public void BeginRender_CriticalArrivedBetweenDequeueAndRender_ClosesRace()
    {
        var core = Core();
        var normal = Enqueue(core, "normal", SpeechPriority.Normal);
        var taken = core.TakeNext(SpeechContext.Unknown, Now);
        Assert.Same(normal, taken.Next);

        // No current item yet, so the enqueue itself cannot pre-empt…
        core.Enqueue(new SpeechRequest("crit", SpeechPriority.Critical), Now, default, out var preempt);
        Assert.False(preempt);

        // …the race is closed at render start instead.
        Assert.True(core.BeginRender(normal));
    }

    [Fact]
    public void HandleCancelled_PreemptedNormal_RestartsAtHead()
    {
        var core = Core();
        var normal = Enqueue(core, "first", SpeechPriority.Normal);
        Enqueue(core, "second", SpeechPriority.Normal);
        core.TakeNext(SpeechContext.Unknown, Now);
        core.BeginRender(normal);
        core.Enqueue(new SpeechRequest("crit", SpeechPriority.Critical), Now, default, out _);

        Assert.Equal(CancelDisposition.Restarted, core.HandleCancelled(normal));

        // Critical first, then the restarted item ahead of "second".
        Assert.Equal("crit", core.TakeNext(SpeechContext.Unknown, Now).Next?.Request.Text);
        Assert.Equal("first", core.TakeNext(SpeechContext.Unknown, Now).Next?.Request.Text);
        Assert.Equal("second", core.TakeNext(SpeechContext.Unknown, Now).Next?.Request.Text);
    }

    [Theory]
    [InlineData(SpeechPriority.High)]
    [InlineData(SpeechPriority.Low)]
    public void HandleCancelled_PreemptedHighOrLow_Superseded(SpeechPriority priority)
    {
        var core = Core();
        var item = Enqueue(core, "text", priority);
        core.TakeNext(SpeechContext.Unknown, Now);
        core.BeginRender(item);
        core.Enqueue(new SpeechRequest("crit", SpeechPriority.Critical), Now, default, out _);

        Assert.Equal(CancelDisposition.Superseded, core.HandleCancelled(item));
    }

    [Fact]
    public void HandleCancelled_PreemptedNormalGoneInvalid_Superseded()
    {
        var core = Core();
        var item = Enqueue(core, "text", SpeechPriority.Normal, valid: () => false);
        core.TakeNext(SpeechContext.Unknown, Now); // Valid checked only at dequeue passes it through…
        // (predicate returns false, so dequeue drops it — enqueue a fresh one to render)
        core = Core();
        var stillValid = true;
        item = Enqueue(core, "text", SpeechPriority.Normal, valid: () => stillValid);
        core.TakeNext(SpeechContext.Unknown, Now);
        core.BeginRender(item);
        core.Enqueue(new SpeechRequest("crit", SpeechPriority.Critical), Now, default, out _);
        stillValid = false;

        Assert.Equal(CancelDisposition.Superseded, core.HandleCancelled(item));
    }

    [Fact]
    public void HandleCancelled_NotPreempted_Cancelled()
    {
        var core = Core();
        var item = Enqueue(core, "text", SpeechPriority.Normal);
        core.TakeNext(SpeechContext.Unknown, Now);
        core.BeginRender(item);

        Assert.Equal(CancelDisposition.Cancelled, core.HandleCancelled(item));
    }

    [Fact]
    public void TakeNext_ExpiredItem_DisposedExpired()
    {
        var core = Core();
        Enqueue(core, "stale", SpeechPriority.Normal, ttl: TimeSpan.FromSeconds(5));

        var result = core.TakeNext(SpeechContext.Unknown, Now.AddSeconds(6));

        Assert.Null(result.Next);
        var disposal = Assert.Single(result.Disposals);
        Assert.Equal(SpeechOutcome.Expired, disposal.Outcome);
    }

    [Fact]
    public void TakeNext_InvalidItem_DisposedDropped()
    {
        var core = Core();
        Enqueue(core, "gone", SpeechPriority.Normal, valid: () => false);

        var result = core.TakeNext(SpeechContext.Unknown, Now);

        Assert.Null(result.Next);
        Assert.Equal(SpeechOutcome.Dropped, Assert.Single(result.Disposals).Outcome);
    }

    [Fact]
    public void TakeNext_CallerCancelled_DisposedDropped()
    {
        var core = Core();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Enqueue(core, "cancelled", SpeechPriority.Normal, ct: cts.Token);

        var result = core.TakeNext(SpeechContext.Unknown, Now);

        Assert.Null(result.Next);
        Assert.Equal(SpeechOutcome.Dropped, Assert.Single(result.Disposals).Outcome);
    }

    [Fact]
    public void TakeNext_SuppressVerdict_DisposedSuppressed()
    {
        var rule = new FixedRule { Verdict = _ => SpeechSuppressionVerdict.Suppress };
        var core = Core(rule);
        Enqueue(core, "quiet", SpeechPriority.Low);

        var result = core.TakeNext(SpeechContext.Unknown, Now);

        Assert.Null(result.Next);
        Assert.Equal(SpeechOutcome.Suppressed, Assert.Single(result.Disposals).Outcome);
    }

    [Fact]
    public void TakeNext_DeferVerdict_HoldsWithoutResolvingCaller()
    {
        var rule = new FixedRule { Verdict = _ => SpeechSuppressionVerdict.Defer };
        var core = Core(rule);
        Enqueue(core, "later", SpeechPriority.Normal);

        var result = core.TakeNext(SpeechContext.Unknown, Now);

        Assert.Null(result.Next);
        var disposal = Assert.Single(result.Disposals);
        Assert.Equal(SpeechEventKind.Deferred, disposal.Kind);
        Assert.Null(disposal.Outcome); // Still pending — caller task untouched.
        Assert.Equal(1, core.DeferredCount);
    }

    [Fact]
    public void TakeNext_DeferredReadmitsToTail_WhenRuleRelents()
    {
        var rule = new FixedRule { Verdict = _ => SpeechSuppressionVerdict.Defer };
        var core = Core(rule);
        Enqueue(core, "held", SpeechPriority.Normal);
        core.TakeNext(SpeechContext.Unknown, Now); // moves "held" to deferred

        rule.Verdict = _ => SpeechSuppressionVerdict.Allow;
        Enqueue(core, "fresh", SpeechPriority.Normal);

        // Readmit goes to the TAIL: the item enqueued while "held" waited plays first.
        Assert.Equal("fresh", core.TakeNext(SpeechContext.Unknown, Now).Next?.Request.Text);
        Assert.Equal("held", core.TakeNext(SpeechContext.Unknown, Now).Next?.Request.Text);
        Assert.Equal(0, core.DeferredCount);
    }

    [Fact]
    public void TakeNext_SuppressedWhileDeferred_DisposedSuppressed()
    {
        var rule = new FixedRule { Verdict = _ => SpeechSuppressionVerdict.Defer };
        var core = Core(rule);
        Enqueue(core, "held", SpeechPriority.Normal);
        core.TakeNext(SpeechContext.Unknown, Now);

        rule.Verdict = _ => SpeechSuppressionVerdict.Suppress;
        var result = core.TakeNext(SpeechContext.Unknown, Now);

        Assert.Equal(SpeechOutcome.Suppressed, Assert.Single(result.Disposals).Outcome);
        Assert.Equal(0, core.DeferredCount);
    }

    [Fact]
    public void Judge_ThrowingRule_IsIgnored()
    {
        var throwing = new FixedRule { Verdict = _ => throw new InvalidOperationException("boom") };
        var core = Core(throwing);
        Enqueue(core, "text", SpeechPriority.Normal);

        Assert.Equal("text", core.TakeNext(SpeechContext.Unknown, Now).Next?.Request.Text);
    }

    [Fact]
    public void Judge_SuppressBeatsDefer()
    {
        var defer = new FixedRule { Verdict = _ => SpeechSuppressionVerdict.Defer };
        var suppress = new FixedRule { Verdict = _ => SpeechSuppressionVerdict.Suppress };
        var core = Core(defer, suppress);
        Enqueue(core, "text", SpeechPriority.Normal);

        var result = core.TakeNext(SpeechContext.Unknown, Now);

        Assert.Equal(SpeechOutcome.Suppressed, Assert.Single(result.Disposals).Outcome);
    }

    [Fact]
    public void Drain_ReturnsQueuedAndDeferred()
    {
        var rule = new FixedRule { Verdict = _ => SpeechSuppressionVerdict.Defer };
        var core = Core(rule);
        Enqueue(core, "held", SpeechPriority.Normal);
        core.TakeNext(SpeechContext.Unknown, Now);
        rule.Verdict = _ => SpeechSuppressionVerdict.Allow;
        Enqueue(core, "queued", SpeechPriority.High);

        var drained = core.Drain();

        Assert.Equal(2, drained.Count);
        Assert.Equal(0, core.QueueDepth);
        Assert.Equal(0, core.DeferredCount);
    }
}
