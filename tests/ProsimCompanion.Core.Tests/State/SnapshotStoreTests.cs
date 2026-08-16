using ProsimCompanion.Core.Collections;
using ProsimCompanion.Core.State;
using Xunit;

namespace ProsimCompanion.Core.Tests.State;

/// <summary>
/// The one store implementation, tested once (campaign #86): snapshot/update semantics, the
/// fire-only-on-change rule, custom comparers, and observer lifecycle. Plus the shared
/// bounded ring the diagnostics store and log buffer sit on.
/// </summary>
public sealed class SnapshotStoreTests
{
    private sealed record Counter(int Value, string Label = "");

    private sealed class CounterStore() : SnapshotStore<Counter>(new Counter(0));

    [Fact]
    public void Update_TransformsTheSnapshot_AndNotifiesWithIt()
    {
        var store = new CounterStore();
        Counter? seen = null;
        using var subscription = store.Observe(snapshot => seen = snapshot);

        store.Update(current => current with { Value = current.Value + 1 });

        Assert.Equal(1, store.Snapshot().Value);
        Assert.Equal(new Counter(1), seen);
    }

    [Fact]
    public void Update_ProducingAnEqualSnapshot_DoesNotNotify()
    {
        var store = new CounterStore();
        var fired = 0;
        using var subscription = store.Observe(_ => fired++);

        store.Update(current => current with { Value = current.Value }); // value-equal record

        Assert.Equal(0, fired);
    }

    [Fact]
    public void CustomComparer_GatesNotification_ButTheSnapshotIsAlwaysStored()
    {
        // The LlmHealthStore shape: one field refreshes every write and must not churn.
        var store = new LabelOnlyStore();
        var fired = 0;
        using var subscription = store.Observe(_ => fired++);

        store.Update(_ => new Counter(1, "same"));
        store.Update(_ => new Counter(2, "same")); // Value changed, comparer ignores it

        Assert.Equal(1, fired);
        Assert.Equal(2, store.Snapshot().Value); // the silent write still landed
    }

    [Fact]
    public void DisposedObserver_StopsReceiving_AndDisposeIsIdempotent()
    {
        var store = new CounterStore();
        var fired = 0;
        var subscription = store.Observe(_ => fired++);

        store.Update(current => current with { Value = 1 });
        subscription.Dispose();
        subscription.Dispose();
        store.Update(current => current with { Value = 2 });

        Assert.Equal(1, fired);
    }

    [Fact]
    public void Observers_AreIndependent()
    {
        var store = new CounterStore();
        var first = 0;
        var second = 0;
        using var one = store.Observe(_ => first++);
        var two = store.Observe(_ => second++);

        store.Update(current => current with { Value = 1 });
        two.Dispose();
        store.Update(current => current with { Value = 2 });

        Assert.Equal(2, first);
        Assert.Equal(1, second);
    }

    private sealed class LabelOnlyStore() : SnapshotStore<Counter>(new Counter(0), new LabelComparer());

    private sealed class LabelComparer : IEqualityComparer<Counter>
    {
        public bool Equals(Counter? x, Counter? y) => x?.Label == y?.Label;

        public int GetHashCode(Counter obj) => obj.Label.GetHashCode(StringComparison.Ordinal);
    }
}

public sealed class BoundedLogTests
{
    [Fact]
    public void Snapshot_IsNewestFirst()
    {
        var log = new BoundedLog<int>(5);
        log.Add(1);
        log.Add(2);
        log.Add(3);

        Assert.Equal([3, 2, 1], log.Snapshot());
    }

    [Fact]
    public void Add_BeyondCapacity_EvictsOldest()
    {
        var log = new BoundedLog<int>(3);
        for (var i = 1; i <= 5; i++)
        {
            log.Add(i);
        }

        Assert.Equal([5, 4, 3], log.Snapshot());
    }
}
