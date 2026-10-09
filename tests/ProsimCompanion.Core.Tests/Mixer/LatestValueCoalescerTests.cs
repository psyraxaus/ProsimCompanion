using ProsimCompanion.Audio.Mixer;
using Xunit;

namespace ProsimCompanion.Core.Tests.Mixer;

public sealed class LatestValueCoalescerTests
{
    [Fact]
    public void Drain_ReturnsOnlyTheLatestValuePerParameter()
    {
        var coalescer = new LatestValueCoalescer();
        coalescer.Offer("Strip[0].Gain", -10);
        coalescer.Offer("Strip[0].Gain", -8);
        coalescer.Offer("Strip[0].Gain", -6);
        coalescer.Offer("Bus[0].Mute", 1);

        var batch = coalescer.Drain();

        Assert.Equal(2, batch.Count);
        Assert.Contains(("Strip[0].Gain", -6.0), batch);
        Assert.Contains(("Bus[0].Mute", 1.0), batch);
        Assert.Empty(coalescer.Drain());
    }

    [Fact]
    public void Offer_OfTheValueAlreadySent_IsSkipped()
    {
        var coalescer = new LatestValueCoalescer();
        coalescer.Offer("Strip[0].Gain", -6);
        _ = coalescer.Drain();

        coalescer.Offer("Strip[0].Gain", -6);

        Assert.Empty(coalescer.Drain());
    }

    [Fact]
    public void Offer_BackToTheSentValue_BeforeADrain_CancelsThePendingWrite()
    {
        var coalescer = new LatestValueCoalescer();
        coalescer.Offer("Strip[0].Gain", -6);
        _ = coalescer.Drain();

        coalescer.Offer("Strip[0].Gain", -5);
        coalescer.Offer("Strip[0].Gain", -6); // knob jittered back within one tick

        Assert.Empty(coalescer.Drain());
    }

    [Fact]
    public void Reset_ForgetsSentValues_SoAReconnectReplaysThem()
    {
        var coalescer = new LatestValueCoalescer();
        coalescer.Offer("Strip[0].Gain", -6);
        _ = coalescer.Drain();

        coalescer.Reset();
        coalescer.Offer("Strip[0].Gain", -6);

        Assert.Single(coalescer.Drain());
    }

    [Fact]
    public void Forget_DropsOneParameter_OthersStaySent()
    {
        var coalescer = new LatestValueCoalescer();
        coalescer.Offer("Strip[0].Gain", -6);
        coalescer.Offer("Bus[0].Mute", 1);
        _ = coalescer.Drain();

        coalescer.Forget("Strip[0].Gain");
        coalescer.Offer("Strip[0].Gain", -6);
        coalescer.Offer("Bus[0].Mute", 1);

        var batch = coalescer.Drain();
        Assert.Single(batch);
        Assert.Equal("Strip[0].Gain", batch[0].Parameter);
    }
}
