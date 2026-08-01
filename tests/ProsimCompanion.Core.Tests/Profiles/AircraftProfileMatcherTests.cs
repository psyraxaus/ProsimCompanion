using ProsimCompanion.Core.Profiles;
using Xunit;

namespace ProsimCompanion.Core.Tests.Profiles;

public sealed class AircraftProfileMatcherTests
{
    private static AircraftProfile Profile(string name, ProfileMatchType type, string match) => new()
    {
        Name = name,
        MatchType = type,
        MatchString = match,
    };

    [Fact]
    public void NullOrEmptyTitle_MatchesNothing()
    {
        var profiles = new[] { Profile("Any", ProfileMatchType.TitleContains, "A320") };

        Assert.Null(AircraftProfileMatcher.Match(profiles, null));
        Assert.Null(AircraftProfileMatcher.Match(profiles, ""));
    }

    [Fact]
    public void Contains_IsCaseInsensitive()
    {
        var profiles = new[] { Profile("Neo", ProfileMatchType.TitleContains, "a320neo") };

        var match = AircraftProfileMatcher.Match(profiles, "FBW A320NEO British Airways");

        Assert.Equal("Neo", match?.Name);
    }

    [Fact]
    public void ExactMatch_BeatsEarlierContainsMatch()
    {
        var profiles = new[]
        {
            Profile("Generic", ProfileMatchType.TitleContains, "A320"),
            Profile("Exact", ProfileMatchType.TitleEquals, "ProSim A320 PSX"),
        };

        var match = AircraftProfileMatcher.Match(profiles, "ProSim A320 PSX");

        Assert.Equal("Exact", match?.Name);
    }

    [Fact]
    public void SameMatchType_FirstInListWins()
    {
        var profiles = new[]
        {
            Profile("First", ProfileMatchType.TitleContains, "A320"),
            Profile("Second", ProfileMatchType.TitleContains, "A320"),
        };

        Assert.Equal("First", AircraftProfileMatcher.Match(profiles, "Some A320 Livery")?.Name);
    }

    [Fact]
    public void EmptyMatchString_NeverMatches()
    {
        var profiles = new[] { Profile("Broken", ProfileMatchType.TitleContains, "") };

        Assert.Null(AircraftProfileMatcher.Match(profiles, "Anything"));
    }

    [Fact]
    public void NoHit_ReturnsNull()
    {
        var profiles = new[] { Profile("737", ProfileMatchType.TitleContains, "737") };

        Assert.Null(AircraftProfileMatcher.Match(profiles, "ProSim A320"));
    }
}
