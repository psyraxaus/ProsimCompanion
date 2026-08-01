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
}
