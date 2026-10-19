using ProsimCompanion.Gsx.Automation;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

/// <summary>
/// The host's lock rule (issue #166): an effect flagged <c>RunsAfterLockRelease</c> is applied
/// only once the pump lock is released — its observers pump again, and a nested pump would
/// bounce off the lock and lose the verdict until the next unrelated edge. Proven with a
/// fake applier that records the lock state it saw for every effect.
/// </summary>
public sealed class LockedPumpRunnerTests : IDisposable
{
    private sealed record Plain(string Name) : DeparturePumpEffect;

    private sealed record Deferred(string Name) : DeparturePumpEffect
    {
        public override bool RunsAfterLockRelease => true;
    }

    private sealed class RecordingApplier(LockedPumpRunner runner) : IDeparturePumpEffectApplier
    {
        public List<(string Name, bool LockHeld)> Seen { get; } = [];

        public Func<DeparturePumpEffect, bool>? Throws { get; set; }

        public void Apply(DeparturePumpEffect effect)
        {
            var name = effect switch
            {
                Plain p => p.Name,
                Deferred d => d.Name,
                _ => effect.GetType().Name,
            };
            Seen.Add((name, runner.IsHeld));
            if (Throws?.Invoke(effect) == true)
            {
                throw new InvalidOperationException($"{name} failed");
            }
        }
    }

    private readonly LockedPumpRunner _runner = new();
    private readonly List<Exception> _errors = [];

    public void Dispose() => _runner.Dispose();

    [Fact]
    public void DeferredEffect_RunsAfterTheLockIsReleased_OthersUnderIt()
    {
        var applier = new RecordingApplier(_runner);

        var ran = _runner.TryRun(
            () => [new Plain("decision"), new Deferred("mark de-ice next"), new Plain("board"), new Plain("dispatch")],
            applier,
            _errors.Add);

        Assert.True(ran);
        Assert.Equal(
            [
                ("decision", true),
                ("board", true),
                ("dispatch", true),
                ("mark de-ice next", false),
            ],
            applier.Seen);
        Assert.Empty(_errors);
        Assert.False(_runner.IsHeld);
    }

    [Fact]
    public void NestedPump_WhileRunning_IsTurnedAway()
    {
        var applier = new RecordingApplier(_runner);
        bool? nested = null;

        _runner.TryRun(
            () =>
            {
                nested = _runner.TryRun(() => [new Plain("nested")], applier, _errors.Add);
                return [new Plain("outer")];
            },
            applier,
            _errors.Add);

        Assert.False(nested);
        Assert.Equal([("outer", true)], applier.Seen);
    }

    [Fact]
    public void DeferredEffect_StillRuns_AfterTheLock_WhenItRanBeforeTheObserverPumpsAgain()
    {
        // The deferred mark's observers pump again at once: that nested pump must now get the
        // lock (the old shell bug was the mark running INSIDE the lock, bouncing the re-pump).
        var applier = new RecordingApplier(_runner);
        var rePumpRan = false;
        var observer = new RePumpingApplier(applier, () => rePumpRan = _runner.TryRun(() => [new Plain("re-pump")], applier, _errors.Add));

        _runner.TryRun(() => [new Plain("decision"), new Deferred("mark")], observer, _errors.Add);

        Assert.True(rePumpRan);
        Assert.Equal([("decision", true), ("mark", false), ("re-pump", true)], applier.Seen);
    }

    private sealed class RePumpingApplier(RecordingApplier inner, Action onDeferred) : IDeparturePumpEffectApplier
    {
        public void Apply(DeparturePumpEffect effect)
        {
            inner.Apply(effect);
            if (effect.RunsAfterLockRelease)
            {
                onDeferred();
            }
        }
    }

    [Fact]
    public void FailingEffect_StopsTheWalk_ReleasesTheLock_RunsOnlyDeferredSeenBeforeIt()
    {
        var applier = new RecordingApplier(_runner) { Throws = e => e is Plain { Name: "board" } };

        _runner.TryRun(
            () => [new Plain("decision"), new Deferred("seen before"), new Plain("board"), new Plain("dispatch"), new Deferred("not reached")],
            applier,
            _errors.Add);

        Assert.Equal([("decision", true), ("board", true), ("seen before", false)], applier.Seen);
        Assert.Single(_errors);
        Assert.False(_runner.IsHeld);
    }

    [Fact]
    public void FailingPlanner_ReportsTheError_AppliesNothing_ReleasesTheLock()
    {
        var applier = new RecordingApplier(_runner);

        var ran = _runner.TryRun(() => throw new InvalidOperationException("gather failed"), applier, _errors.Add);

        Assert.True(ran);
        Assert.Empty(applier.Seen);
        Assert.Single(_errors);
        Assert.False(_runner.IsHeld);
    }

    [Fact]
    public void FailingDeferredEffect_IsReported_NotThrown()
    {
        var applier = new RecordingApplier(_runner) { Throws = e => e is Deferred };

        _runner.TryRun(() => [new Deferred("mark")], applier, _errors.Add);

        Assert.Single(_errors);
        Assert.False(_runner.IsHeld);
    }
}
