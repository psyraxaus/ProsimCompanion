using ProsimCompanion.Core.Configuration;
using Xunit;

namespace ProsimCompanion.Core.Tests.Configuration;

/// <summary>v2 → v3 (2026-10-13): the old <c>day.rotationsFolder</c> default "rotations" (a
/// folder under the exe nothing ever created) becomes "" = the seeded per-user folder.</summary>
public sealed class SettingsMigratorRotationsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"pc-migrate-{Guid.NewGuid():N}.json");

    [Fact]
    public void Old_default_becomes_empty()
    {
        File.WriteAllText(_path, """{ "configVersion": 2, "day": { "rotationsFolder": "rotations", "autoTrack": true } }""");
        var file = new JsonSettingsFile(_path);

        var previous = SettingsMigrator.Migrate(file);

        Assert.Equal(2, previous);
        var root = file.Read();
        Assert.Equal(SettingsMigrator.CurrentVersion, (int?)root["configVersion"]);
        Assert.Equal("", (string?)root["day"]!["rotationsFolder"]);
        Assert.Equal(true, (bool?)root["day"]!["autoTrack"]);
    }

    [Fact]
    public void A_custom_folder_is_kept()
    {
        File.WriteAllText(_path, """{ "configVersion": 2, "day": { "rotationsFolder": "D:\\plans" } }""");
        var file = new JsonSettingsFile(_path);

        SettingsMigrator.Migrate(file);

        Assert.Equal("D:\\plans", (string?)file.Read()["day"]!["rotationsFolder"]);
    }

    [Fact]
    public void No_day_section_is_fine()
    {
        File.WriteAllText(_path, """{ "configVersion": 2, "prosim": { "host": "simpc" } }""");
        var file = new JsonSettingsFile(_path);

        SettingsMigrator.Migrate(file);

        Assert.Equal(SettingsMigrator.CurrentVersion, (int?)file.Read()["configVersion"]);
    }

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }
}
