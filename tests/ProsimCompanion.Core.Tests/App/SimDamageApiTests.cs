using ProsimCompanion.App.Hosting;
using ProsimCompanion.Core.Aircraft;
using Xunit;

namespace ProsimCompanion.Core.Tests.App;

public sealed class SimDamageApiTests
{
    /// <summary>Recording fake: hands out one stub per name and counts registrations so the
    /// subscribe-once contract is assertable.</summary>
    private sealed class StubSimVars : ISimVars
    {
        public readonly Dictionary<string, StubSubscription> Subscriptions = new(StringComparer.OrdinalIgnoreCase);
        public int SubscribeCalls;

        public IDataRefSubscription SubscribeDynamic(string simVarName, string unit, DataRefTier tier)
        {
            SubscribeCalls++;
            var subscription = new StubSubscription { Name = simVarName };
            Subscriptions[simVarName] = subscription;
            return subscription;
        }

        public Task WriteAsync(string simVarName, double value, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class StubSubscription : IDataRefSubscription
    {
        public required string Name { get; init; }
        public object? RawValue { get; set; }
        public bool IsStale { get; set; }
        public DateTimeOffset? LastUpdatedUtc { get; set; }
        public bool Disposed;

        public event EventHandler? ValueChanged { add { } remove { } }

        public T GetValue<T>(T fallback) => RawValue is T typed ? typed : fallback;

        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void FirstSnapshot_SubscribesOnce_AndServesBenignFallbacks()
    {
        var simVars = new StubSimVars();
        using var probe = new SimDamageProbe(simVars);

        var first = probe.Snapshot();
        var second = probe.Snapshot();

        Assert.Equal(9, simVars.SubscribeCalls);
        Assert.Equal(first.Count, second.Count);

        // Never-received wear levels must read as healthy (catalog fallback 1.0), not 0=failed.
        var tireWear = Assert.Single(first, entry => entry.Key == "tireWear");
        Assert.Equal("WEAR AND TEAR LEVEL:37", tireWear.SimVar);
        Assert.Equal(1.0, tireWear.Value);
        Assert.False(tireWear.Received);

        var gearDamage = Assert.Single(first, entry => entry.Key == "gearDamageBySpeed");
        Assert.Equal(0.0, gearDamage.Value);
        Assert.False(gearDamage.Received);
    }

    [Fact]
    public void ReceivedValues_SurfaceRawStateAndTimestamps()
    {
        var simVars = new StubSimVars();
        using var probe = new SimDamageProbe(simVars);
        _ = probe.Snapshot();

        var pushedAt = DateTimeOffset.UtcNow;
        var wear = simVars.Subscriptions["WEAR AND TEAR LEVEL:37"];
        wear.RawValue = 0.12;
        wear.LastUpdatedUtc = pushedAt;
        var failed = simVars.Subscriptions["WEAR AND TEAR IS FAILED:37"];
        failed.RawValue = true;
        failed.LastUpdatedUtc = pushedAt;

        var snapshot = probe.Snapshot();

        var tireWear = Assert.Single(snapshot, entry => entry.Key == "tireWear");
        Assert.Equal(0.12, tireWear.Value);
        Assert.True(tireWear.Received);
        Assert.Equal(pushedAt, tireWear.LastUpdatedUtc);

        var tireFailed = Assert.Single(snapshot, entry => entry.Key == "tireFailed");
        Assert.Equal(1.0, tireFailed.Value);
        Assert.True(tireFailed.Received);
    }

    [Fact]
    public void Dispose_ReleasesEveryHandle()
    {
        var simVars = new StubSimVars();
        var probe = new SimDamageProbe(simVars);
        _ = probe.Snapshot();

        probe.Dispose();

        Assert.All(simVars.Subscriptions.Values, subscription => Assert.True(subscription.Disposed));
    }

    [Fact]
    public void BuildResponse_CarriesConnectionFlagAndSemanticsNote()
    {
        var response = SimDamageEndpoints.BuildResponse(msfsConnected: true, values: []);

        Assert.True(response.MsfsConnected);
        Assert.Equal(SimDamageEndpoints.Note, response.Note);
        Assert.Empty(response.Values);
    }
}
