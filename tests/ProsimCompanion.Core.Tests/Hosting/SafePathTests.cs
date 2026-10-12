using ProsimCompanion.Core.Hosting;
using Xunit;

namespace ProsimCompanion.Core.Tests.Hosting;

/// <summary>The shared gate in front of the file APIs (review 2026-10-13): a user-typed
/// path never throws out of Exists or GetFullPath.</summary>
public sealed class SafePathTests
{
    [Theory]
    [InlineData("  \"C:\\x\"  ", "C:\\x")]
    [InlineData("C:\\x", "C:\\x")]
    [InlineData(null, "")]
    public void Unquote_strips_explorer_quotes_and_whitespace(string? value, string expected)
        => Assert.Equal(expected, SafePath.Unquote(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("C:\\bad\0name")]
    public void Malformed_paths_are_refused_everywhere(string? path)
    {
        Assert.False(SafePath.IsWellFormed(path));
        Assert.Null(SafePath.TryGetFullPath(path));
        Assert.False(SafePath.DirectoryExists(path));
        Assert.False(SafePath.FileExists(path));
    }

    [Fact]
    public void Relative_paths_resolve_under_the_base_directory()
        => Assert.Equal("C:\\app\\config\\x.json", SafePath.TryGetFullPath("config\\x.json", "C:\\app"));

    [Fact]
    public void Exists_answers_without_throwing()
    {
        var folder = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        var file = Path.GetTempFileName();
        try
        {
            Assert.True(SafePath.DirectoryExists(folder));
            Assert.True(SafePath.FileExists(file));
            Assert.False(SafePath.FileExists(folder));
            Assert.False(SafePath.DirectoryExists(Path.Combine(folder, "gone-" + Guid.NewGuid().ToString("N"))));
        }
        finally
        {
            File.Delete(file);
        }
    }
}
