using ProsimCompanion.Core.Diagnostics.Metrics;
using Xunit;

namespace ProsimCompanion.Core.Tests.Diagnostics.Ecam;

public sealed class MetricsTests
{
    [Fact]
    public void LatencyTracker_Empty_HasNoFigures()
    {
        var t = new LatencyTracker();
        Assert.Null(t.Last);
        Assert.Null(t.P50);
        Assert.Null(t.P95);
        Assert.Equal(0, t.Count);
    }

    [Fact]
    public void LatencyTracker_PercentilesOnAKnownSeries()
    {
        var t = new LatencyTracker();
        for (var i = 1; i <= 100; i++)
        {
            t.Record(i);
        }

        // Only the last 64 are kept: 37..100.
        Assert.Equal(64, t.Count);
        Assert.Equal(100, t.Last);
        Assert.Equal(68, t.P50);  // nearest rank 32 of 37..100
        Assert.Equal(97, t.P95);  // nearest rank 61 of 37..100
    }

    [Fact]
    public void LatencyTracker_IgnoresNegativeAndNaN()
    {
        var t = new LatencyTracker();
        t.Record(-1);
        t.Record(double.NaN);
        Assert.Equal(0, t.Count);
        t.Record(12.5);
        Assert.Equal(12.5, t.Last);
        Assert.Equal(12.5, t.P95);
    }

    [Fact]
    public void RateCounter_WindowRollsOff()
    {
        var now = new DateTimeOffset(2026, 10, 27, 6, 0, 0, TimeSpan.Zero);
        var counter = new RateCounter(() => now);

        for (var i = 0; i < 50; i++)
        {
            counter.Hit();
        }

        Assert.Equal(10, counter.PerSecond, 3);

        now = now.AddSeconds(3);
        Assert.Equal(10, counter.PerSecond, 3);

        now = now.AddSeconds(2.5);
        Assert.Equal(0, counter.PerSecond, 3);

        counter.Hit();
        Assert.Equal(0.2, counter.PerSecond, 3);
    }

    [Fact]
    public void RateCounter_SpreadHits_AverageOverTheWindow()
    {
        var now = new DateTimeOffset(2026, 10, 27, 6, 0, 0, TimeSpan.Zero);
        var counter = new RateCounter(() => now);
        for (var i = 0; i < 20; i++)
        {
            counter.Hit();
            now = now.AddMilliseconds(100);
        }

        // 20 hits over the last 2 s inside a 5 s window.
        Assert.Equal(4, counter.PerSecond, 3);
    }

    [Fact]
    public void Registries_CreateOnFirstUse_AndShareByName()
    {
        var latency = new LatencyRegistry();
        latency[LatencyNames.Asr].Record(5);
        Assert.Same(latency[LatencyNames.Asr], latency["asr"]);
        Assert.Equal(5, latency[LatencyNames.Asr].Last);
        Assert.Contains("asr", latency.Names);

        var rates = new RateRegistry();
        rates[RateNames.Api].Hit();
        Assert.Same(rates[RateNames.Api], rates["api"]);
    }

    [Fact]
    public void UptimeTracker_NeverNegative()
    {
        var start = new DateTimeOffset(2026, 10, 27, 6, 0, 0, TimeSpan.Zero);
        var up = new UptimeTracker(start);
        Assert.Equal(TimeSpan.FromMinutes(2), up.Elapsed(start.AddMinutes(2)));
        Assert.Equal(TimeSpan.Zero, up.Elapsed(start.AddMinutes(-2)));
    }
}
