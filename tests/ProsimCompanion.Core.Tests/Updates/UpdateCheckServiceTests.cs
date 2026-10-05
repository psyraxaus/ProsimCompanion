using ProsimCompanion.Core.Updates;
using Xunit;

namespace ProsimCompanion.Core.Tests.Updates;

public sealed class UpdateCheckServiceTests
{
    [Theory]
    [InlineData("v0.2.0", "0.1.0", true)]
    [InlineData("0.2.0", "0.1.0", true)]
    [InlineData("V1.0.0", "0.9.9", true)]
    [InlineData("v0.1.0", "0.1.0", false)]
    [InlineData("v0.1.0", "0.2.0", false)]
    [InlineData("nightly", "0.1.0", false)] // unparseable tag is never "newer"
    [InlineData("", "0.1.0", false)]
    [InlineData("v0.2.0", "garbage", false)] // nor is anything "newer" than an unreadable running version
    public void IsNewer_ComparesSemverTags(string tag, string current, bool expected)
        => Assert.Equal(expected, UpdateCheckService.IsNewer(tag, current));

    /// <summary>Issue #159 (ticket t-20261005-1918): every release is tagged v…-rc.N, the tag
    /// never parsed, and a 0.6.0-rc.16 user was told "update available: False" with rc.18
    /// published.</summary>
    [Theory]
    [InlineData("v0.6.0-rc.18", "0.6.0-rc.16", true)] // the field case
    [InlineData("v0.6.0-rc.18", "0.6.0-rc.18", false)]
    [InlineData("v0.6.0-rc.16", "0.6.0-rc.18", false)]
    [InlineData("v0.6.0-rc.10", "0.6.0-rc.2", true)] // numbers compare as numbers, not as text
    [InlineData("v0.6.0-rc.2", "0.6.0-rc.10", false)]
    [InlineData("v0.6.0-rc.1", "0.6.0-beta.9", true)] // rc is after beta
    [InlineData("v0.6.0-beta.9", "0.6.0-rc.1", false)]
    [InlineData("v0.6.0-rc.1", "0.5.0-rc.9", true)] // the core version decides first
    [InlineData("v0.5.0-rc.9", "0.6.0-rc.1", false)]
    [InlineData("v0.6.0-rc.18", "0.4.0-rc.2", true)]
    public void IsNewer_ComparesPreReleaseParts(string tag, string current, bool expected)
        => Assert.Equal(expected, UpdateCheckService.IsNewer(tag, current));

    [Theory]
    [InlineData("v0.2.0", "0.2.0-beta", true)] // the final release is newer than its pre-releases
    [InlineData("v0.6.0", "0.6.0-rc.18", true)]
    [InlineData("v0.6.0-rc.18", "0.6.0", false)]
    [InlineData("v0.6.1-rc.1", "0.6.0", true)]
    public void IsNewer_FinalReleaseIsAboveItsPreReleases(string tag, string current, bool expected)
        => Assert.Equal(expected, UpdateCheckService.IsNewer(tag, current));

    [Theory]
    [InlineData("v0.6.0-rc.18", "0.6.0-rc.18+e58785d", false)] // build metadata is not a version
    [InlineData("v0.6.0-rc.18", "0.6.0-rc.16+e58785d", true)]
    [InlineData("v0.6.0-rc.18+abc", "0.6.0-rc.18+def", false)]
    [InlineData("v0.6.0-", "0.5.0", false)] // an empty pre-release identifier is unparseable
    [InlineData("v0.6", "0.6.0", false)] // 0.6 and 0.6.0 are the same release
    public void IsNewer_IgnoresBuildMetadata_AndRejectsMalformedTags(string tag, string current, bool expected)
        => Assert.Equal(expected, UpdateCheckService.IsNewer(tag, current));
}
