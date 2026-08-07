using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Day;
using ProsimCompanion.Core.Tests.TechLog;
using Xunit;

namespace ProsimCompanion.Core.Tests.Day;

public sealed class DayStateFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pc-daystate-").FullName;
    private readonly DayStateFile _file;

    public DayStateFileTests()
    {
        var options = new DayOptions { Path = Path.Combine(_dir, "daystate.json") };
        _file = new DayStateFile(OptionsSupport.Monitor(options), NullLogger<DayStateFile>.Instance);
    }

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
    public void Load_NoFile_ReturnsNull() => Assert.Null(_file.Load());

    [Fact]
    public void SaveAndLoad_RoundTripsTheDay()
    {
        var day = new DayState
        {
            DayId = "day-1",
            StartedUtc = "2026-08-08T06:00:00.0000000+00:00",
            DutyStartUtc = "2026-08-08T06:00:00.0000000+00:00",
            State = DayPhase.Turnaround,
            Mode = DayMode.Planned,
            CurrentLegIndex = 2,
        };
        day.Legs.Add(new DayLeg { Index = 1, From = "YSSY", To = "YMML", BlockMinutes = 80, Stabilized = true });
        day.Legs.Add(new DayLeg { Index = 2, From = "YMML" });

        _file.Save(day);
        var loaded = _file.Load();

        Assert.NotNull(loaded);
        Assert.Equal("day-1", loaded.DayId);
        Assert.Equal(DayPhase.Turnaround, loaded.State);
        Assert.Equal(DayMode.Planned, loaded.Mode);
        Assert.Equal(2, loaded.CurrentLegIndex);
        Assert.Equal(2, loaded.Legs.Count);
        Assert.Equal(80, loaded.Legs[0].BlockMinutes);
        Assert.True(loaded.Legs[0].Stabilized);
        // Atomic write: no .tmp left behind.
        Assert.False(File.Exists(Path.Combine(_dir, "daystate.json.tmp")));
    }

    [Fact]
    public void Load_CorruptFile_MovesAsideWithUnifiedNamingAndReturnsNull()
    {
        var path = Path.Combine(_dir, "daystate.json");
        File.WriteAllText(path, "{ not json at all");

        Assert.Null(_file.Load());
        Assert.False(File.Exists(path));
        // Unified corrupt-aside naming (the predecessor used a one-off ".bad-*" scheme here).
        Assert.Single(Directory.GetFiles(_dir, "daystate.json.corrupt-*.bak"));
    }
}
