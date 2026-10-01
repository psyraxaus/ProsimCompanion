using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.Speech.Tts;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>Per-provider usage counters: Google keeps the historical usage.json (migration
/// compatible), every other provider gets its own file, and neither reads the other's tally.</summary>
public sealed class TtsUsageTrackerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tts-usage-tests-{Guid.NewGuid():N}");
    private readonly TtsUsageTracker _tracker = new(NullLogger<TtsUsageTracker>.Instance);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void GoogleAndElevenLabs_WriteDifferentFiles_AndDoNotCrossContaminate()
    {
        _tracker.Increment(_root, 100);
        _tracker.Increment(_root, 7, "elevenlabs");
        _tracker.Increment(_root, 3, "elevenlabs");

        Assert.True(File.Exists(Path.Combine(_root, "usage.json")));
        Assert.True(File.Exists(Path.Combine(_root, "usage.elevenlabs.json")));

        Assert.Equal(100, _tracker.CharactersThisMonth(_root));
        Assert.Equal(100, _tracker.CharactersThisMonth(_root, "google"));
        Assert.Equal(10, _tracker.CharactersThisMonth(_root, "elevenlabs"));
    }

    [Fact]
    public void Budget_IsPerProvider()
    {
        _tracker.Increment(_root, 9_000, "elevenlabs");

        Assert.True(_tracker.IsOverBudget(_root, 9_000, 1, "elevenlabs"));
        Assert.False(_tracker.IsOverBudget(_root, 9_000, 1)); // Google untouched.
        Assert.False(_tracker.IsOverBudget(_root, 0, 1, "elevenlabs")); // 0 disables.
    }

    [Fact]
    public void FileName_IsMigrationCompatibleForGoogle()
    {
        Assert.Equal("usage.json", TtsUsageTracker.FileNameFor("google"));
        Assert.Equal("usage.json", TtsUsageTracker.FileNameFor("Google"));
        Assert.Equal("usage.elevenlabs.json", TtsUsageTracker.FileNameFor("elevenlabs"));
    }
}
