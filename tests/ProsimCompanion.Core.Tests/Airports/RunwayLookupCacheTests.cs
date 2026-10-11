using ProsimCompanion.Core.Airports;
using ProsimCompanion.Core.Flight;
using Xunit;

namespace ProsimCompanion.Core.Tests.Airports;

/// <summary>Issue #175: a runway the gateway could not answer was re-asked on every GSX store
/// push (1 435 round-trips in one leg). A miss is now remembered for a while.</summary>
public sealed class RunwayLookupCacheTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 11, 0, 0, 0, TimeSpan.Zero);
    private static readonly RunwayGeometry Egcc23L = new("EGCC", "23L", new GeoPoint(53.37, -2.26), 233, 10_000, "gateway");

    [Fact]
    public void Key_NormalisesAndCarriesThePlan()
    {
        Assert.Equal("EGCC|23L|abc", RunwayLookupCache.Key(" egcc", "23l ", "abc"));
        Assert.Equal("EGCC|23L|", RunwayLookupCache.Key("EGCC", "23L", null));
    }

    [Fact]
    public void Hit_IsKeptForTheKeysLifetime()
    {
        var cache = new RunwayLookupCache();
        var key = RunwayLookupCache.Key("EGCC", "23L", "r1");
        cache.Store(key, Egcc23L, T0);

        Assert.True(cache.TryGet(key, T0.AddHours(3), out var hit));
        Assert.Same(Egcc23L, hit);
        Assert.False(cache.TryGet(RunwayLookupCache.Key("EGCC", "23L", "r2"), T0, out _)); // a new plan is a new question
    }

    [Fact]
    public void Miss_IsTrustedUntilTheRetryWindow_ThenAskedOnceMore()
    {
        var cache = new RunwayLookupCache();
        var key = RunwayLookupCache.Key("EGCC", "04R", "r1");
        cache.Store(key, null, T0);

        Assert.True(cache.TryGet(key, T0.AddSeconds(1), out var miss));
        Assert.Null(miss);
        Assert.True(cache.TryGet(key, T0.Add(RunwayLookupCache.MissRetryAfter).AddSeconds(-1), out _));
        Assert.False(cache.TryGet(key, T0.Add(RunwayLookupCache.MissRetryAfter), out _));
    }

    [Fact]
    public void UnknownKey_IsNotAnAnswer()
        => Assert.False(new RunwayLookupCache().TryGet("EGCC|23L|", T0, out _));
}
