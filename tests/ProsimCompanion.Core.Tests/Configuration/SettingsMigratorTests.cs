using ProsimCompanion.Core.Configuration;
using Xunit;

namespace ProsimCompanion.Core.Tests.Configuration;

public sealed class SettingsMigratorTests : IDisposable
{
    private readonly string _directory;
    private readonly string _path;

    public SettingsMigratorTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "ProsimCompanionTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "settings.json");
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void Migrate_MissingFile_CreatesVersionedFile()
    {
        var file = new JsonSettingsFile(_path);

        var previous = SettingsMigrator.Migrate(file);

        Assert.Equal(0, previous);
        Assert.Equal(SettingsMigrator.CurrentVersion, (int?)file.Read()["configVersion"]);
    }

    [Fact]
    public void Migrate_UnversionedFile_StampsAndPreservesContent()
    {
        File.WriteAllText(_path, """{ "prosim": { "host": "simpc" } }""");
        var file = new JsonSettingsFile(_path);

        var previous = SettingsMigrator.Migrate(file);

        Assert.Equal(0, previous);
        var root = file.Read();
        Assert.Equal(SettingsMigrator.CurrentVersion, (int?)root["configVersion"]);
        Assert.Equal("simpc", (string?)root["prosim"]?["host"]);
    }

    [Fact]
    public void Migrate_CurrentVersion_DoesNotRewriteFile()
    {
        var file = new JsonSettingsFile(_path);
        SettingsMigrator.Migrate(file);
        var firstWrite = File.GetLastWriteTimeUtc(_path);

        var previous = SettingsMigrator.Migrate(file);

        Assert.Equal(SettingsMigrator.CurrentVersion, previous);
        Assert.Equal(firstWrite, File.GetLastWriteTimeUtc(_path));
    }

    [Fact]
    public void MigrateV1_ConcurrentOrder_BecomesAfterCalledSteps_BoardingAfterAll()
    {
        File.WriteAllText(_path, """
            {
              "configVersion": 1,
              "gsx": {
                "departureServiceOrder": ["Refueling", "Catering", "Boarding"],
                "concurrentServices": true,
                "boardingAfter": []
              }
            }
            """);
        var file = new JsonSettingsFile(_path);

        SettingsMigrator.Migrate(file);

        var gsx = file.Read()["gsx"]!;
        var steps = gsx["departureServices"]!.AsArray();
        Assert.Equal(3, steps.Count);
        Assert.Equal("Refueling", (string?)steps[0]!["service"]);
        Assert.Equal("afterCalled", (string?)steps[0]!["activation"]);
        Assert.Equal("afterCalled", (string?)steps[1]!["activation"]);
        Assert.Equal("Boarding", (string?)steps[2]!["service"]);
        Assert.Equal("afterAllCompleted", (string?)steps[2]!["activation"]);
        Assert.Null(gsx["departureServiceOrder"]);
        Assert.Null(gsx["concurrentServices"]);
        Assert.Null(gsx["boardingAfter"]);
    }

    [Fact]
    public void MigrateV1_SequentialOrder_BecomesAfterPrevCompletedSteps()
    {
        File.WriteAllText(_path, """
            {
              "configVersion": 1,
              "gsx": {
                "departureServiceOrder": ["Refueling", "Catering"],
                "concurrentServices": false
              }
            }
            """);
        var file = new JsonSettingsFile(_path);

        SettingsMigrator.Migrate(file);

        var steps = file.Read()["gsx"]!["departureServices"]!.AsArray();
        Assert.Equal("afterPrevCompleted", (string?)steps[0]!["activation"]);
        Assert.Equal("afterPrevCompleted", (string?)steps[1]!["activation"]);
    }

    [Fact]
    public void MigrateV1_NoLegacyKeys_AddsNothing()
    {
        File.WriteAllText(_path, """{ "configVersion": 1, "gsx": { "enabled": true } }""");
        var file = new JsonSettingsFile(_path);

        SettingsMigrator.Migrate(file);

        var gsx = file.Read()["gsx"]!;
        Assert.Null(gsx["departureServices"]); // the defaults writer fills this in afterwards
        Assert.Equal(true, (bool?)gsx["enabled"]);
    }
}
