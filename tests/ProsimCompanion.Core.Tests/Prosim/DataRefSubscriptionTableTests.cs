using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Prosim.DataRefs;
using Xunit;

namespace ProsimCompanion.Core.Tests.Prosim;

public sealed class DataRefSubscriptionTableTests
{
    private readonly DataRefSubscriptionTable _table = new();

    [Fact]
    public void Subscribe_NewName_RaisesRegistrationNeeded()
    {
        var registrations = new List<(string Name, int IntervalMs)>();
        _table.RegistrationNeeded += (name, interval) => registrations.Add((name, interval));

        using var subscription = _table.Subscribe("aircraft.speed.ias", DataRefTier.Frequent);

        Assert.Equal([("aircraft.speed.ias", 250)], registrations);
    }

    [Fact]
    public void Subscribe_SameNameSlowerTier_SharesRegistrationWithoutReRegistering()
    {
        var registrations = 0;
        _table.RegistrationNeeded += (_, _) => registrations++;

        using var fast = _table.Subscribe("doors.entry.left.fwd", DataRefTier.Normal);
        using var slow = _table.Subscribe("doors.entry.left.fwd", DataRefTier.Infrequent);

        Assert.Equal(1, registrations);
        Assert.Equal([("doors.entry.left.fwd", 500)], _table.ActiveRegistrations());
    }

    [Fact]
    public void Subscribe_FasterTierLater_SpeedsUpSharedRegistration()
    {
        var registrations = new List<int>();
        _table.RegistrationNeeded += (_, interval) => registrations.Add(interval);

        using var slow = _table.Subscribe("aircraft.altitude", DataRefTier.Infrequent);
        using var fast = _table.Subscribe("aircraft.altitude", DataRefTier.Critical);

        Assert.Equal([2000, 100], registrations);
        Assert.Equal([("aircraft.altitude", 100)], _table.ActiveRegistrations());
    }

    [Fact]
    public void UpdateValue_NotifiesSubscribersAndCaches()
    {
        using var subscription = _table.Subscribe("aircraft.fuel.total.amount.kg", DataRefTier.Normal);
        var notified = 0;
        subscription.ValueChanged += (_, _) => notified++;

        var timestamp = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        _table.UpdateValue("aircraft.fuel.total.amount.kg", 6250.0, timestamp);

        Assert.Equal(1, notified);
        Assert.Equal(6250.0, subscription.GetValue(0.0));
        Assert.False(subscription.IsStale);
        Assert.Equal(timestamp, subscription.LastUpdatedUtc);
    }

    [Fact]
    public void MarkAllStale_FlagsButRetainsValues()
    {
        using var subscription = _table.Subscribe("aircraft.speed.ias", DataRefTier.Frequent);
        _table.UpdateValue("aircraft.speed.ias", 145.0, DateTimeOffset.UtcNow);

        _table.MarkAllStale();

        Assert.True(subscription.IsStale);
        Assert.Equal(145.0, subscription.GetValue(0.0));
    }

    [Fact]
    public void UpdateValue_AfterStale_ClearsStaleFlag()
    {
        using var subscription = _table.Subscribe("aircraft.speed.ias", DataRefTier.Frequent);
        _table.UpdateValue("aircraft.speed.ias", 145.0, DateTimeOffset.UtcNow);
        _table.MarkAllStale();

        _table.UpdateValue("aircraft.speed.ias", 150.0, DateTimeOffset.UtcNow);

        Assert.False(subscription.IsStale);
        Assert.Equal(150.0, subscription.GetValue(0.0));
    }

    [Fact]
    public void Dispose_LastSubscriber_RaisesRegistrationReleased()
    {
        var released = new List<string>();
        _table.RegistrationReleased += released.Add;

        var first = _table.Subscribe("efb.chocks", DataRefTier.Normal);
        var second = _table.Subscribe("efb.chocks", DataRefTier.Normal);

        first.Dispose();
        Assert.Empty(released);

        second.Dispose();
        Assert.Equal(["efb.chocks"], released);
        Assert.Empty(_table.ActiveRegistrations());
    }

    [Fact]
    public void Notify_SubscriberThrows_OtherSubscribersStillNotified()
    {
        var errors = new List<string>();
        var table = new DataRefSubscriptionTable((name, _) => errors.Add(name));
        using var bad = table.Subscribe("aircraft.speed.ias", DataRefTier.Frequent);
        using var good = table.Subscribe("aircraft.speed.ias", DataRefTier.Frequent);
        bad.ValueChanged += (_, _) => throw new InvalidOperationException("boom");
        var goodNotified = 0;
        good.ValueChanged += (_, _) => goodNotified++;

        table.UpdateValue("aircraft.speed.ias", 100.0, DateTimeOffset.UtcNow);

        Assert.Equal(1, goodNotified);
        Assert.Equal(["aircraft.speed.ias"], errors);
    }

    [Fact]
    public void UpdateValue_UnknownName_IsIgnored()
        => _table.UpdateValue("never.subscribed", 1, DateTimeOffset.UtcNow);
}
