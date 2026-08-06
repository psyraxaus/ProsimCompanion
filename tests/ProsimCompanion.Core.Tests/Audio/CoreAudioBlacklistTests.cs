using ProsimCompanion.Audio.Backends.CoreAudio;
using Xunit;

namespace ProsimCompanion.Core.Tests.Audio;

public sealed class CoreAudioBlacklistTests
{
    [Theory]
    // An entry suppresses every device whose friendly name STARTS WITH it (case-insensitive).
    [InlineData("Speakers (Realtek High Definition Audio)", "Speakers (Realtek", true)]
    [InlineData("Speakers (Realtek High Definition Audio)", "speakers", true)]
    [InlineData("Speakers (Realtek High Definition Audio)", "Realtek", false)]
    [InlineData("Headphones", "Speakers", false)]
    public void IsBlacklisted_MatchesOnDeviceNamePrefix(string deviceName, string entry, bool expected)
    {
        Assert.Equal(expected, CoreAudioDeviceRegistry.IsBlacklisted(deviceName, [entry]));
    }

    [Fact]
    public void BlankEntries_NeverMatch()
    {
        Assert.False(CoreAudioDeviceRegistry.IsBlacklisted("Speakers", ["", "   "]));
        Assert.False(CoreAudioDeviceRegistry.IsBlacklisted("Speakers", []));
    }

    [Fact]
    public void EntriesAreTrimmedBeforeMatching()
    {
        Assert.True(CoreAudioDeviceRegistry.IsBlacklisted("Speakers (Realtek)", ["  Speakers "]));
    }
}
