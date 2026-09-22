using System.Text.Json.Nodes;
using ProsimCompanion.Core.Configuration;
using Xunit;

namespace ProsimCompanion.Core.Tests.Configuration;

/// <summary>
/// DPAPI only exists on Windows; the round-trip cases return early elsewhere (the CI matrix
/// may run on Linux) while the pass-through and failure cases run everywhere.
/// </summary>
public sealed class SecretProtectorTests : IDisposable
{
    private readonly string _directory;
    private readonly string _path;

    public SecretProtectorTests()
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
    public void Protect_ThenTryUnprotect_RoundTrips()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var stored = SecretProtector.Protect("sk-live-1234");

        Assert.StartsWith(SecretProtector.Prefix, stored, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-live-1234", stored, StringComparison.Ordinal);
        Assert.True(SecretProtector.TryUnprotect(stored, out var plain));
        Assert.Equal("sk-live-1234", plain);
    }

    [Fact]
    public void Protect_IsNonDeterministic_SoStoredStringsMustNeverBeCompared()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.NotEqual(SecretProtector.Protect("same"), SecretProtector.Protect("same"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("dpapi:already")]
    public void Protect_EmptyOrAlreadyProtected_IsNoOp(string value)
    {
        Assert.Equal(value, SecretProtector.Protect(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("plain-key")]
    public void TryUnprotect_PlainValue_PassesThrough(string? stored)
    {
        Assert.True(SecretProtector.TryUnprotect(stored, out var value));
        Assert.Equal(stored, value);
    }

    [Theory]
    [InlineData("dpapi:not base64!!")]
    [InlineData("dpapi:AAAA")]
    public void TryUnprotect_UndecryptableValue_ReturnsFalse(string stored)
    {
        Assert.False(SecretProtector.TryUnprotect(stored, out var value));
        Assert.Null(value);
    }

    [Fact]
    public void ProtectKnownSecrets_ProtectsPlainValues_AndLeavesTheRestAlone()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = (JsonObject)JsonNode.Parse("""
            {
              "prosim": { "host": "simpc", "apiKey": "plain-key" },
              "sayIntentions": { "manualApiKey": "" },
              "briefing": { "llmApiKey": "dpapi:untouched" }
            }
            """)!;

        Assert.True(SecretProtector.ProtectKnownSecrets(root));

        Assert.Equal("simpc", (string?)root["prosim"]?["host"]);
        var apiKey = (string?)root["prosim"]?["apiKey"];
        Assert.True(SecretProtector.IsProtected(apiKey));
        Assert.True(SecretProtector.TryUnprotect(apiKey, out var plain));
        Assert.Equal("plain-key", plain);
        Assert.Equal("", (string?)root["sayIntentions"]?["manualApiKey"]);
        Assert.Equal("dpapi:untouched", (string?)root["briefing"]?["llmApiKey"]);
    }

    [Fact]
    public void ProtectKnownSecrets_NothingPlain_ReturnsFalse()
    {
        var root = (JsonObject)JsonNode.Parse("""
            { "prosim": { "apiKey": "dpapi:x" }, "webUi": { "port": 5320 }, "briefing": 3 }
            """)!;

        Assert.False(SecretProtector.ProtectKnownSecrets(root));
        Assert.Equal("dpapi:x", (string?)root["prosim"]?["apiKey"]);
    }

    [Fact]
    public void JsonSettingsFile_Update_ProtectsSecretsOnWrite()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var file = new JsonSettingsFile(_path);

        file.Update(root =>
            JsonSettingsFile.GetOrCreateSection(root, "sayIntentions")["manualApiKey"] = "si-key");

        var text = File.ReadAllText(_path);
        Assert.DoesNotContain("si-key", text, StringComparison.Ordinal);
        var stored = (string?)new JsonSettingsFile(_path).Read()["sayIntentions"]?["manualApiKey"];
        Assert.True(SecretProtector.TryUnprotect(stored, out var plain));
        Assert.Equal("si-key", plain);
    }

    [Fact]
    public void EnsureProtected_WritesOnlyWhenSomethingIsPlain()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(_directory);
        File.WriteAllText(_path, """{ "prosim": { "apiKey": "plain-key", "host": "simpc" } }""");
        var file = new JsonSettingsFile(_path);

        Assert.True(SecretProtector.EnsureProtected(file));
        Assert.DoesNotContain("plain-key", File.ReadAllText(_path), StringComparison.Ordinal);
        Assert.Equal("simpc", (string?)file.Read()["prosim"]?["host"]);

        var afterFirstPass = File.GetLastWriteTimeUtc(_path);
        Assert.False(SecretProtector.EnsureProtected(file));
        Assert.Equal(afterFirstPass, File.GetLastWriteTimeUtc(_path));
    }
}
