using Microsoft.Extensions.Configuration;
using ProsimCompanion.App.Configuration;
using ProsimCompanion.Core.Configuration;
using Xunit;

namespace ProsimCompanion.Core.Tests.Configuration;

/// <summary>
/// The read side of settings secrets. Every case that asserts on
/// <see cref="SecretProtector.Unreadable"/> lives in this one class: the set is process-wide,
/// and xunit runs the tests of a class sequentially.
/// </summary>
public sealed class ProtectedJsonConfigurationProviderTests : IDisposable
{
    private readonly string _directory;
    private readonly string _path;

    public ProtectedJsonConfigurationProviderTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "ProsimCompanionTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "settings.json");
    }

    public void Dispose()
    {
        SecretProtector.RecordLoad([]);
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Load_UndecryptableSecret_BindsEmptyAndIsRecordedAsUnreadable()
    {
        File.WriteAllText(_path, """
            { "prosim": { "host": "simpc", "apiKey": "dpapi:AAAA" }, "briefing": { "llmApiKey": "plain-llm" } }
            """);

        var configuration = Build();

        Assert.Equal("", configuration["prosim:apiKey"]);
        Assert.Equal("simpc", configuration["prosim:host"]);
        Assert.Equal("plain-llm", configuration["briefing:llmApiKey"]);
        Assert.Contains("prosim:apiKey", SecretProtector.Unreadable);
        Assert.DoesNotContain("briefing:llmApiKey", SecretProtector.Unreadable);
    }

    [Fact]
    public void Load_ClearsUnreadable_WhenTheValueBecomesReadable()
    {
        File.WriteAllText(_path, """{ "prosim": { "apiKey": "dpapi:AAAA" } }""");
        Build();
        Assert.Contains("prosim:apiKey", SecretProtector.Unreadable);

        File.WriteAllText(_path, """{ "prosim": { "apiKey": "re-entered" } }""");
        var configuration = Build();

        Assert.Equal("re-entered", configuration["prosim:apiKey"]);
        Assert.Empty(SecretProtector.Unreadable);
    }

    [Fact]
    public void Load_ProtectedSecret_BindsDecryptedValue()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var stored = SecretProtector.Protect("ws-key");
        File.WriteAllText(_path, $$"""{ "sayIntentions": { "manualApiKey": "{{stored}}" } }""");

        var configuration = Build();

        Assert.Equal("ws-key", configuration["sayIntentions:manualApiKey"]);
        Assert.Empty(SecretProtector.Unreadable);
    }

    [Fact]
    public void Load_MissingFile_IsOptional()
    {
        var configuration = Build();

        Assert.Null(configuration["prosim:apiKey"]);
    }

    private IConfigurationRoot Build()
        => new ConfigurationBuilder()
            .Add(new ProtectedJsonConfigurationSource { Path = _path, Optional = true })
            .Build();
}
