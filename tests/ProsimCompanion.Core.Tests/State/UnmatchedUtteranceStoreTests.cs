using ProsimCompanion.Core.State;
using Xunit;

namespace ProsimCompanion.Core.Tests.State;

/// <summary>The heard-but-not-understood store (issue #112): grouping, ordering, the
/// sterile exclusion and the memory bound.</summary>
public sealed class UnmatchedUtteranceStoreTests
{
    private static UnmatchedUtterance Miss(string text, int secondsAgo, string context = "idle", bool suppressed = false)
        => new(
            new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero).AddSeconds(-secondsAgo),
            text, text.ToLowerInvariant(), 0.4, context, "Cruise", "reject", suppressed);

    [Fact]
    public void Groups_MostRepeatedFirst_ThenMostRecent_SuppressedCountedButNotListed()
    {
        var store = new UnmatchedUtteranceStore();
        store.Record(Miss("set the parking brake", 50));
        store.Record(Miss("set the parking brake", 40, "checklist: Parking Brake"));
        store.Record(Miss("set the parking brake", 30));
        store.Record(Miss("gear up", 20));
        store.Record(Miss("lights off", 10));
        store.Record(Miss("rotate", 5, suppressed: true));

        var snapshot = store.Snapshot();
        var groups = snapshot.Groups();

        Assert.Equal(6, snapshot.Total);
        Assert.Equal(1, snapshot.SuppressedTotal);
        Assert.Equal(["set the parking brake", "lights off", "gear up"], groups.Select(g => g.Normalized).ToArray());
        Assert.Equal(3, groups[0].Count);
        Assert.Equal(["idle", "checklist: Parking Brake"], groups[0].Contexts);
        Assert.DoesNotContain(groups, g => g.Normalized == "rotate");
    }

    [Fact]
    public void Capacity_IsBounded_AndClearForgetsEverything()
    {
        var store = new UnmatchedUtteranceStore();
        for (var i = 0; i < UnmatchedUtteranceStore.Capacity + 25; i++)
        {
            store.Record(Miss($"phrase {i}", i));
        }

        var snapshot = store.Snapshot();
        Assert.Equal(UnmatchedUtteranceStore.Capacity, snapshot.Recent.Count);
        Assert.Equal(UnmatchedUtteranceStore.Capacity + 25, snapshot.Total);         // the count keeps going
        Assert.Equal($"phrase {UnmatchedUtteranceStore.Capacity + 24}", snapshot.Recent[0].Text); // newest first

        var changes = 0;
        using (store.Observe(_ => changes++))
        {
            store.Clear();
        }

        Assert.Equal(1, changes);
        Assert.Equal(UnmatchedUtteranceSnapshot.Empty, store.Snapshot());
    }
}
