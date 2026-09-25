using System.Text.Json;
using ProsimCompanion.Core.Diagnostics;
using ProsimCompanion.Core.EventLog;
using Xunit;

namespace ProsimCompanion.Core.Tests.Diagnostics;

public sealed class AppBuildInfoTests
{
    [Fact]
    public void Create_SplitsVersionAndCommitAtThePlus()
    {
        var build = AppBuildInfo.Create("0.5.0-rc.2+1a2b3c4d", ".NET 10.0.1", "Windows", "X64");

        Assert.Equal("0.5.0-rc.2", build.Version);
        Assert.Equal("0.5.0-rc.2+1a2b3c4d", build.InformationalVersion);
        Assert.Equal("1a2b3c4d", build.Commit);
        Assert.Contains("0.5.0-rc.2 (1a2b3c4d)", build.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Create_WithoutMetadata_HasNoCommit()
    {
        var build = AppBuildInfo.Create("0.5.0-rc.2", ".NET 10.0.1", "Windows", "X64");

        Assert.Equal("0.5.0-rc.2", build.Version);
        Assert.Null(build.Commit);
        Assert.Contains("(no commit)", build.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void FromAssembly_CoreAssembly_IsAParseableVersion()
    {
        // The repository version from Directory.Build.props, not the test host's.
        var build = AppBuildInfo.FromAssembly(typeof(AppBuildInfo).Assembly);

        Assert.True(Version.TryParse(build.Version.Split('-')[0], out _), $"'{build.Version}' should parse");
        Assert.DoesNotContain('+', build.Version);
        Assert.False(string.IsNullOrWhiteSpace(build.Runtime));
        Assert.False(string.IsNullOrWhiteSpace(build.Os));
    }

    [Fact]
    public void Current_IsResolvedOnce()
        => Assert.Same(AppBuildInfo.Current, AppBuildInfo.Current);

    private static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public void SessionHeader_RecordsFileNameOnly_AndSerializesCamelCase()
    {
        var build = AppBuildInfo.Create("0.5.0-rc.2+abc", ".NET 10.0.1", "Windows", "X64");

        var header = SessionHeader.Create(build, @"C:\Users\pilot\sessions\session-20260926-120000.jsonl", 1, "A320 IAE");

        Assert.Equal("session-20260926-120000.jsonl", header.SessionFile);
        var json = JsonSerializer.Serialize(header, CamelCase);
        Assert.Contains("\"appVersion\":\"0.5.0-rc.2\"", json, StringComparison.Ordinal);
        Assert.Contains("\"commit\":\"abc\"", json, StringComparison.Ordinal);
        Assert.Contains("\"sampleIntervalSeconds\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"activeProfile\":\"A320 IAE\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("pilot", json, StringComparison.Ordinal);
    }
}
