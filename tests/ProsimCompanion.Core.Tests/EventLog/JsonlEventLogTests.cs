using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.Core.EventLog;
using Xunit;

namespace ProsimCompanion.Core.Tests.EventLog;

public sealed class JsonlEventLogTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pc-eventlog-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task StartNewSession_RotatesTheFileAndSplitsEventsCorrectly()
    {
        var log = new JsonlEventLog(_dir, NullLogger<JsonlEventLog>.Instance);
        var firstPath = log.Path;

        log.Record("leg-one-event", new { leg = 1 });
        log.StartNewSession();
        var secondPath = log.Path;
        log.Record("leg-two-event", new { leg = 2 });
        await log.DisposeAsync();

        // Path changed immediately and the files are distinct.
        Assert.NotEqual(firstPath, secondPath);
        Assert.True(File.Exists(firstPath));
        Assert.True(File.Exists(secondPath));

        // Events recorded before the rotation drained to the old file, later ones to the new.
        var first = File.ReadAllText(firstPath);
        var second = File.ReadAllText(secondPath);
        Assert.Contains("leg-one-event", first, StringComparison.Ordinal);
        Assert.DoesNotContain("leg-two-event", first, StringComparison.Ordinal);
        Assert.Contains("leg-two-event", second, StringComparison.Ordinal);
        Assert.DoesNotContain("leg-one-event", second, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartNewSession_SameSecond_NeverCollides()
    {
        var log = new JsonlEventLog(_dir, NullLogger<JsonlEventLog>.Instance);
        var paths = new List<string> { log.Path };

        // Several rotations within one second must yield unique files (suffix scheme).
        for (var i = 0; i < 3; i++)
        {
            log.StartNewSession();
            paths.Add(log.Path);
        }

        await log.DisposeAsync();
        Assert.Equal(paths.Count, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(paths, p => Assert.True(File.Exists(p)));
    }
}
