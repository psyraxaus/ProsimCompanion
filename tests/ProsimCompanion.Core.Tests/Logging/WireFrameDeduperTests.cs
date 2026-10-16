using ProsimCompanion.Core.Logging;
using Xunit;

namespace ProsimCompanion.Core.Tests.Logging;

/// <summary>Identical consecutive wire frames on one link are folded; the next different
/// frame reports the count (2026-10-17).</summary>
public sealed class WireFrameDeduperTests
{
    [Fact]
    public void FirstFrame_IsWritten_RepeatsAreFolded_NextDifferentFrameCarriesTheCount()
    {
        var deduper = new WireFrameDeduper();

        Assert.True(deduper.Accept("GsxRemoteApi", "<<", "{startup}", out var suppressed));
        Assert.Equal(0, suppressed);
        Assert.False(deduper.Accept("GsxRemoteApi", "<<", "{startup}", out _));
        Assert.False(deduper.Accept("GsxRemoteApi", "<<", "{startup}", out _));
        Assert.True(deduper.Accept("GsxRemoteApi", "<<", "{state}", out suppressed));
        Assert.Equal(2, suppressed);
        // The count is reported once.
        Assert.True(deduper.Accept("GsxRemoteApi", "<<", "{startup}", out suppressed));
        Assert.Equal(0, suppressed);
    }

    [Fact]
    public void Links_AreIndependent_ByChannelAndDirection()
    {
        var deduper = new WireFrameDeduper();

        Assert.True(deduper.Accept("Gateway", ">>", "{q}", out _));
        Assert.True(deduper.Accept("Gateway", "<<", "{q}", out _));
        Assert.True(deduper.Accept("Mixer", ">>", "{q}", out _));
        Assert.False(deduper.Accept("Gateway", ">>", "{q}", out _));
    }

    [Fact]
    public void Reset_ForgetsTheLinks_SoTheNextFrameIsWritten()
    {
        var deduper = new WireFrameDeduper();
        deduper.Accept("Mixer", "<<", "{welcome}", out _);

        deduper.Reset();

        Assert.True(deduper.Accept("Mixer", "<<", "{welcome}", out var suppressed));
        Assert.Equal(0, suppressed);
    }
}
