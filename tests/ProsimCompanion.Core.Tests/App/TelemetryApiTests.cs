using ProsimCompanion.App.Hosting;
using Xunit;

namespace ProsimCompanion.Core.Tests.App;

/// <summary>
/// File-serving mechanics of the telemetry API (issue #94). The security property under test:
/// a requested name resolves ONLY by exact match against the directory's enumerated files, so
/// traversal strings can never reach the filesystem as a path. Tail reads must tolerate a
/// live writer and never hand a consumer a torn first line.
/// </summary>
public sealed class TelemetryApiTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("telemetry-api-tests").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void TryResolve_ExactName_ResolvesInsideTheDirectory()
    {
        var path = Path.Combine(_dir, "session-20260817-071244.jsonl");
        File.WriteAllText(path, "{}\n");

        Assert.True(TelemetryApiEndpoints.TryResolve(_dir, "session-20260817-071244.jsonl", out var resolved));
        Assert.Equal(path, resolved);
    }

    [Theory]
    [InlineData("..\\settings.json")]
    [InlineData("../settings.json")]
    [InlineData("..")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-such-file.jsonl")]
    public void TryResolve_TraversalAndMisses_AreRefused(string requested)
    {
        File.WriteAllText(Path.Combine(_dir, "session-20260817-071244.jsonl"), "{}\n");

        Assert.False(TelemetryApiEndpoints.TryResolve(_dir, requested, out _));
    }

    [Fact]
    public void TryResolve_MissingDirectory_RefusesQuietly()
        => Assert.False(TelemetryApiEndpoints.TryResolve(
            Path.Combine(_dir, "never-created"), "x.log", out _));

    [Fact]
    public void ListFiles_MissingDirectory_IsEmpty()
        => Assert.Empty(TelemetryApiEndpoints.ListFiles(Path.Combine(_dir, "never-created")));

    [Fact]
    public void ListFiles_NewestFirst_WithSizes()
    {
        var older = Path.Combine(_dir, "older.log");
        var newer = Path.Combine(_dir, "newer.log");
        File.WriteAllText(older, "aa");
        File.WriteAllText(newer, "bbbb");
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddHours(-2));
        File.SetLastWriteTimeUtc(newer, DateTime.UtcNow);

        var files = TelemetryApiEndpoints.ListFiles(_dir);

        Assert.Equal(["newer.log", "older.log"], files.Select(file => file.Name).ToArray());
        Assert.Equal(4, files[0].SizeBytes);
    }

    [Fact]
    public void ReadTail_SmallFile_ComesBackWhole()
    {
        var path = Path.Combine(_dir, "app.log");
        File.WriteAllText(path, "line one\nline two\n");

        Assert.Equal("line one\nline two\n", TelemetryApiEndpoints.ReadTail(path, tailKb: 64));
    }

    [Fact]
    public void ReadTail_LargeFile_DropsTheTornFirstLine()
    {
        var path = Path.Combine(_dir, "wire.log");
        // 200 lines of ~100 chars ≈ 20 KB; a 1 KB tail must start on a line boundary.
        File.WriteAllLines(path, Enumerable.Range(1, 200).Select(i => $"line {i:D4} {new string('x', 90)}"));

        var tail = TelemetryApiEndpoints.ReadTail(path, tailKb: 1);

        Assert.StartsWith("line ", tail, StringComparison.Ordinal);
        Assert.True(tail.Length <= 1024);
        Assert.EndsWith($"line 0200 {new string('x', 90)}{Environment.NewLine}", tail, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadTail_FileHeldOpenByAWriter_StillReads()
    {
        var path = Path.Combine(_dir, "live.log");
        using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        writer.Write("held open\n"u8);
        writer.Flush();

        Assert.Equal("held open\n", TelemetryApiEndpoints.ReadTail(path, tailKb: 64));
    }

    [Fact]
    public void BuildSummary_VersionCanary_AndNewestNonWireLog()
    {
        var sessions = new[]
        {
            new TelemetryFileView("session-b.jsonl", 10, DateTimeOffset.UtcNow),
            new TelemetryFileView("session-a.jsonl", 10, DateTimeOffset.UtcNow.AddHours(-1)),
        };
        var logs = new[]
        {
            new TelemetryFileView("ProsimCompanion-wire-20260818.log", 999, DateTimeOffset.UtcNow),
            new TelemetryFileView("ProsimCompanion-20260818.log", 42, DateTimeOffset.UtcNow.AddMinutes(-1)),
        };

        var summary = TelemetryApiEndpoints.BuildSummary(sessions, logs);

        Assert.Equal(2, summary.SessionFileCount);
        Assert.Equal("session-b.jsonl", summary.CurrentSession?.Name);
        // The wire trace is newer but the APP log is what the probes read first.
        Assert.Equal("ProsimCompanion-20260818.log", summary.CurrentLog?.Name);
        Assert.False(string.IsNullOrWhiteSpace(summary.Version));
        Assert.DoesNotContain('+', summary.Version);
    }
}
