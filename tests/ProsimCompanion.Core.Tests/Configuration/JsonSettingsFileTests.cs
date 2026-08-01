using System.Text.Json.Nodes;
using ProsimCompanion.Core.Configuration;
using Xunit;

namespace ProsimCompanion.Core.Tests.Configuration;

public sealed class JsonSettingsFileTests : IDisposable
{
    private readonly string _directory;
    private readonly string _path;

    public JsonSettingsFileTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "ProsimCompanionTests", Guid.NewGuid().ToString("N"));
        _path = Path.Combine(_directory, "settings.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Read_WhenFileMissing_ReturnsEmptyObject()
    {
        var file = new JsonSettingsFile(_path);

        var root = file.Read();

        Assert.Empty(root);
    }

    [Fact]
    public void Update_CreatesFileAndPersistsValues()
    {
        var file = new JsonSettingsFile(_path);

        file.Update(root =>
        {
            var prosim = JsonSettingsFile.GetOrCreateSection(root, "prosim");
            prosim["host"] = "simpc";
        });

        var reread = new JsonSettingsFile(_path).Read();
        Assert.Equal("simpc", (string?)reread["prosim"]?["host"]);
    }

    [Fact]
    public void Update_PreservesUnrelatedSections()
    {
        File.WriteAllText(CreateDirectoryAndPath(), """{ "gsx": { "enabled": false }, "custom": 42 }""");
        var file = new JsonSettingsFile(_path);

        file.Update(root =>
        {
            var webUi = JsonSettingsFile.GetOrCreateSection(root, "webUi");
            webUi["port"] = 6000;
        });

        var reread = file.Read();
        Assert.Equal(6000, (int?)reread["webUi"]?["port"]);
        Assert.False((bool?)reread["gsx"]?["enabled"]);
        Assert.Equal(42, (int?)reread["custom"]);
    }

    [Fact]
    public void GetOrCreateSection_ReturnsExistingSection()
    {
        var root = new JsonObject { ["prosim"] = new JsonObject { ["host"] = "kept" } };

        var section = JsonSettingsFile.GetOrCreateSection(root, "prosim");

        Assert.Equal("kept", (string?)section["host"]);
    }

    private string CreateDirectoryAndPath()
    {
        Directory.CreateDirectory(_directory);
        return _path;
    }
}
