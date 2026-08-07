using ProsimCompanion.Core.Day;
using Xunit;

namespace ProsimCompanion.Core.Tests.Day;

public sealed class RotationLoaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pc-rotations-").FullName;

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
    public void TryLoad_MissingFolder_ReturnsNull()
        => Assert.Null(RotationLoader.TryLoad(Path.Combine(_dir, "nope")));

    [Fact]
    public void TryLoad_TakesFirstFileInOrdinalOrder()
    {
        File.WriteAllText(Path.Combine(_dir, "b-day.json"),
            """{ "dayId": "B", "legs": [ { "from": "EGCC", "to": "EGLL" } ] }""");
        File.WriteAllText(Path.Combine(_dir, "a-day.json"),
            """{ "dayId": "A", "legs": [ { "from": "EGLL", "to": "EGCC", "flightNo": "BA123", "scheduledOffUtc": "2026-07-16T07:15:00Z", "scheduledOnUtc": "2026-07-16T08:05:00Z" } ] }""");

        var rotation = RotationLoader.TryLoad(_dir);
        Assert.NotNull(rotation);
        Assert.Equal("A", rotation.DayId);
        var leg = Assert.Single(rotation.Legs);
        Assert.Equal("EGLL", leg.From);
        Assert.Equal("EGCC", leg.To);
        Assert.Equal("BA123", leg.FlightNo);
        Assert.Equal("2026-07-16T07:15:00Z", leg.ScheduledOffUtc);
    }

    [Fact]
    public void TryLoad_SkipsMalformedAndEmptyCandidates()
    {
        // The predecessor loaded only the first file and silently lost the plan when it was
        // bad; the loader must fall through to the next usable candidate.
        File.WriteAllText(Path.Combine(_dir, "1-broken.json"), "{ not json");
        File.WriteAllText(Path.Combine(_dir, "2-empty.json"), """{ "dayId": "E", "legs": [] }""");
        File.WriteAllText(Path.Combine(_dir, "3-good.json"),
            """{ "dayId": "G", "reportTimeUtc": "2026-07-16T06:30:00Z", "legs": [ { "from": "EGLL", "to": "EGCC" } ] }""");

        var rotation = RotationLoader.TryLoad(_dir);
        Assert.NotNull(rotation);
        Assert.Equal("G", rotation.DayId);
        Assert.Equal("2026-07-16T06:30:00Z", rotation.ReportTimeUtc);
    }

    [Fact]
    public void TryLoad_NothingUsable_ReturnsNull()
    {
        File.WriteAllText(Path.Combine(_dir, "empty.json"), """{ "legs": [] }""");
        Assert.Null(RotationLoader.TryLoad(_dir));
    }
}
