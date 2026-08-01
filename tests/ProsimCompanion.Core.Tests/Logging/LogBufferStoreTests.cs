using ProsimCompanion.Core.Logging;
using Xunit;

namespace ProsimCompanion.Core.Tests.Logging;

public sealed class LogBufferStoreTests
{
    private static LogEntry Entry(string level, string message = "msg")
        => new(DateTimeOffset.UtcNow, level, "Test.Source", message, null);

    [Fact]
    public void Snapshot_ReturnsNewestFirst()
    {
        var store = new LogBufferStore();
        store.Add(Entry("Information", "first"));
        store.Add(Entry("Information", "second"));

        var snapshot = store.Snapshot();

        Assert.Equal("second", snapshot[0].Message);
        Assert.Equal("first", snapshot[1].Message);
    }

    [Fact]
    public void Add_BeyondCapacity_EvictsOldest()
    {
        var store = new LogBufferStore();
        for (var i = 0; i < LogBufferStore.Capacity + 10; i++)
        {
            store.Add(Entry("Information", $"m{i}"));
        }

        var snapshot = store.Snapshot();

        Assert.Equal(LogBufferStore.Capacity, snapshot.Count);
        Assert.Equal($"m{LogBufferStore.Capacity + 9}", snapshot[0].Message);
    }

    [Fact]
    public void Counters_TrackWarningsAndErrorsAcrossEviction()
    {
        var store = new LogBufferStore();
        store.Add(Entry("Warning"));
        store.Add(Entry("Error"));
        store.Add(Entry("Fatal"));
        store.Add(Entry("Information"));

        Assert.Equal(1, store.WarningCount);
        Assert.Equal(2, store.ErrorCount);
    }
}
