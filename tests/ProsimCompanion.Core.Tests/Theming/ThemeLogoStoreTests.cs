using ProsimCompanion.Core.Theming;
using Xunit;

namespace ProsimCompanion.Core.Tests.Theming;

/// <summary>The pilot's own airline logos (owner decision 2026-09-20: never shipped).</summary>
public sealed class ThemeLogoStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pc-logos-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("KLM Royal Dutch", "klm-royal-dutch")]
    [InlineData("  Swiss  International ", "swiss-international")]
    [InlineData("Air France / KLM", "air-france-klm")]
    [InlineData("***", "theme")]
    public void Slug_IsLowerCaseDashed(string name, string expected)
        => Assert.Equal(expected, ThemeLogoStore.Slug(name));

    [Fact]
    public async Task Save_ThenFind_ThenRemove_RoundTrips()
    {
        var store = new ThemeLogoStore(_directory);
        var changes = 0;
        store.Changed += () => changes++;

        var path = await store.SaveAsync("KLM Royal Dutch", "klm.PNG", new MemoryStream([1, 2, 3]), CancellationToken.None);

        Assert.Equal(Path.Combine(_directory, "klm-royal-dutch.png"), path);
        Assert.Equal(path, store.FindFile("KLM Royal Dutch"));
        Assert.Equal(path, store.FindFileBySlug("klm-royal-dutch"));
        Assert.Equal("image/png", ThemeLogoStore.ContentType(path));
        Assert.Equal(1, store.Version);

        Assert.True(store.Remove("KLM Royal Dutch"));
        Assert.Null(store.FindFile("KLM Royal Dutch"));
        Assert.False(store.Remove("KLM Royal Dutch"));
        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task Save_ReplacesAPreviousLogoOfAnotherFormat()
    {
        var store = new ThemeLogoStore(_directory);
        await store.SaveAsync("Lufthansa", "lh.png", new MemoryStream([1]), CancellationToken.None);

        var svg = await store.SaveAsync("Lufthansa", "lh.svg", new MemoryStream([2]), CancellationToken.None);

        Assert.Equal(svg, store.FindFile("Lufthansa"));
        Assert.False(File.Exists(Path.Combine(_directory, "lufthansa.png")));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task Save_RejectsNonImageExtensions_AndOversizedFiles()
    {
        var store = new ThemeLogoStore(_directory);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.SaveAsync("X", "logo.exe", new MemoryStream([1]), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SaveAsync("X", "logo.png", new MemoryStream(new byte[ThemeLogoStore.MaxBytes + 1]), CancellationToken.None));
        Assert.Null(store.FindFile("X"));
        Assert.False(Directory.Exists(_directory) && Directory.GetFiles(_directory).Length > 0);
    }

    [Fact]
    public void FindFileBySlug_NeverAcceptsAPathFragment()
    {
        var store = new ThemeLogoStore(_directory);
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(Path.Combine(_directory, "secret.png"), [1]);

        Assert.Null(store.FindFileBySlug("../secret"));
        Assert.Null(store.FindFileBySlug("Secret"));
        Assert.NotNull(store.FindFileBySlug("secret"));
    }
}
