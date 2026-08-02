using ProsimCompanion.Core.Configuration;
using Xunit;

namespace ProsimCompanion.Core.Tests.Configuration;

public sealed class SettingsDefaultsWriterTests : IDisposable
{
    private readonly string _directory;
    private readonly string _path;

    public SettingsDefaultsWriterTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "ProsimCompanionTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "settings.json");
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void EnsureDefaults_EmptyFile_WritesEveryOptionWithDefault()
    {
        var file = new JsonSettingsFile(_path);

        var changed = SettingsDefaultsWriter.EnsureDefaults(file);

        Assert.True(changed);
        var root = file.Read();
        Assert.Equal(5320, (int?)root[WebUiOptions.SectionName]?["port"]);
        Assert.Equal("localhost", (string?)root[ProsimOptions.SectionName]?["host"]);
        Assert.Equal(true, (bool?)root[GsxOptions.SectionName]?["enabled"]);
        Assert.Equal(false, (bool?)root[GsxOptions.SectionName]?["allowDefuel"]);
        Assert.Equal("Both", (string?)root[GsxOptions.SectionName]?["crewBoardingAnswer"]);
        Assert.Equal("Information", (string?)root[LoggingOptions.SectionName]?["defaultLevel"]);
    }

    [Fact]
    public void EnsureDefaults_ExistingValues_AreNeverOverwritten()
    {
        File.WriteAllText(_path, """{ "gsx": { "enabled": false, "refuelRateKgPerSec": 40 } }""");
        var file = new JsonSettingsFile(_path);

        SettingsDefaultsWriter.EnsureDefaults(file);

        var root = file.Read();
        Assert.Equal(false, (bool?)root["gsx"]?["enabled"]);
        Assert.Equal(40, (double?)root["gsx"]?["refuelRateKgPerSec"]);
        // New keys still added alongside the preserved ones.
        Assert.NotNull(root["gsx"]?["departureServices"]);
    }

    [Fact]
    public void EnsureDefaults_CompleteFile_MakesNoChanges()
    {
        var file = new JsonSettingsFile(_path);
        SettingsDefaultsWriter.EnsureDefaults(file);
        var firstWrite = File.GetLastWriteTimeUtc(_path);

        var changedAgain = SettingsDefaultsWriter.EnsureDefaults(file);

        Assert.False(changedAgain);
        Assert.Equal(firstWrite, File.GetLastWriteTimeUtc(_path));
    }
}
