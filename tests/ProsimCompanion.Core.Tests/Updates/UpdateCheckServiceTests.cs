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
    [InlineData("v0.2.0", "0.2.0-beta", false)] // 3-component compare: a pre-release suffix is ignored (predecessor rule)
    [InlineData("nightly", "0.1.0", false)] // unparseable tag is never "newer"
    [InlineData("", "0.1.0", false)]
    public void IsNewer_ComparesSemverTags(string tag, string current, bool expected)
        => Assert.Equal(expected, UpdateCheckService.IsNewer(tag, current));

    [Fact]
    public void CurrentVersion_IsAParseableVersion()
    {
        var current = UpdateCheckService.CurrentVersion;
        Assert.True(Version.TryParse(current.Split('-')[0], out _), $"'{current}' should parse");
    }
}
