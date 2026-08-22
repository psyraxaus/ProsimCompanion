using ProsimCompanion.Core.Aircraft;
using Xunit;

namespace ProsimCompanion.Core.Tests.Aircraft;

/// <summary>
/// The sim clock's value contract (issue #95): zulu time-of-day is the authority, the sim
/// date refines it, and anything not live degrades to null so consumers fall back to the
/// real clock instead of freezing on the last simulated minute.
/// </summary>
public sealed class SimClockTests
{
    [Fact]
    public void NoData_IsNotLive_AndFallsBackToRealUtc()
    {
        using var clock = new SimClock(new FakeProsimDataRefs());

        Assert.Null(clock.SimUtcNow);
        Assert.True((DateTimeOffset.UtcNow - clock.UtcNowOrReal).Duration() < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void ZuluAlone_UsesTodaysRealDate()
    {
        var dataRefs = new FakeProsimDataRefs();
        using var clock = new SimClock(dataRefs);
        dataRefs.Push(ProsimDataRefNames.ZuluTime.Name, new TimeSpan(14, 30, 0));

        var sim = clock.SimUtcNow;
        Assert.NotNull(sim);
        Assert.Equal(new TimeSpan(14, 30, 0), sim.Value.TimeOfDay);
        Assert.Equal(DateTime.UtcNow.Date, sim.Value.UtcDateTime.Date);
        Assert.Equal(sim.Value, clock.UtcNowOrReal);
    }

    [Fact]
    public void SimDate_RefinesTheDate_ButItsClockComponentIsIgnored()
    {
        var dataRefs = new FakeProsimDataRefs();
        using var clock = new SimClock(dataRefs);
        dataRefs.Push(ProsimDataRefNames.ZuluTime.Name, new TimeSpan(2, 15, 0));
        // simulator.time's own time-of-day is unverified local-vs-zulu — only the date counts.
        dataRefs.Push(ProsimDataRefNames.SimulatorTime.Name, new DateTime(2026, 12, 24, 23, 59, 0));

        Assert.Equal(new DateTimeOffset(2026, 12, 24, 2, 15, 0, TimeSpan.Zero), clock.SimUtcNow);
    }

    [Fact]
    public void NumericZuluSeconds_AreAccepted()
    {
        // SimConnect's convention: seconds since midnight, sometimes delivered as a double.
        var dataRefs = new FakeProsimDataRefs();
        using var clock = new SimClock(dataRefs);
        dataRefs.Push(ProsimDataRefNames.ZuluTime.Name, 3_600.0);

        Assert.Equal(new TimeSpan(1, 0, 0), clock.SimUtcNow?.TimeOfDay);
    }

    private sealed class FakeProsimDataRefs : IProsimDataRefs
    {
        private readonly Dictionary<string, FakeSubscription> _subscriptions = new(StringComparer.Ordinal);

        public void Push(string name, object value) => _subscriptions[name].RaisePush(value);

        public IDataRefSubscription SubscribeDynamic(string name, DataRefTier tier)
        {
            var subscription = new FakeSubscription(name);
            _subscriptions[name] = subscription;
            return subscription;
        }

        public Task WriteAsync(string name, object? value, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task PressMomentaryAsync(string name, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class FakeSubscription(string name) : IDataRefSubscription
    {
        public string Name => name;
        public object? RawValue { get; private set; }
        public bool IsStale => false;
        public DateTimeOffset? LastUpdatedUtc { get; private set; }

        public event EventHandler? ValueChanged;

        public T GetValue<T>(T fallback) => DataRefCoercion.Coerce(RawValue, fallback);

        public void RaisePush(object value)
        {
            RawValue = value;
            LastUpdatedUtc = DateTimeOffset.UtcNow;
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
        }
    }
}
