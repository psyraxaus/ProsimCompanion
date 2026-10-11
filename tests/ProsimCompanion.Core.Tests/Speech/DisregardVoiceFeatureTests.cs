using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Crew;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>"Disregard" as an idle utterance (issue #168): silences the current advisory and
/// is always acknowledged, so the word never lands in the heard-but-not-understood list.</summary>
public sealed class DisregardVoiceFeatureTests
{
    private readonly FakeArbiter _arbiter = new();
    private readonly SpokenAdvisories _advisories = new();

    private DisregardVoiceFeature Feature()
        => new(_advisories, _arbiter, SpeechTestSupport.TempEventLog(), NullLogger<DisregardVoiceFeature>.Instance);

    [Theory]
    [InlineData("disregard")]
    [InlineData("Disregard.")]
    [InlineData("cancel that")]
    [InlineData("belay that")]
    [InlineData("never mind")]
    public void CancelPhrase_IsHandled_AndAcknowledged(string heard)
    {
        var handled = Feature().TryHandle(heard);

        Assert.True(handled);
        var ack = Assert.Single(_arbiter.Requests);
        Assert.Equal(DisregardVoiceFeature.Acknowledgement, ack.Text);
        Assert.Equal(SpeechPriority.High, ack.Priority);
        Assert.Equal("fo.disregard", ack.Tag);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("negative")]
    [InlineData("request boarding")]
    public void OtherWords_AreNotClaimed(string heard)
    {
        Assert.False(Feature().TryHandle(heard));
        Assert.Empty(_arbiter.Requests);
    }

    [Fact]
    public void Disregard_CancelsTheCurrentAdvisory()
    {
        var token = _advisories.Begin("fo.aircraft-state");
        Assert.Equal("fo.aircraft-state", _advisories.CurrentTag);

        Feature().TryHandle("disregard");

        Assert.True(token.IsCancellationRequested);
        Assert.Null(_advisories.CurrentTag);
    }

    [Fact]
    public void Disregard_WithNothingPlaying_StillAcknowledges()
    {
        Assert.True(Feature().TryHandle("disregard"));
        Assert.Single(_arbiter.Requests);
    }

    [Fact]
    public void SpokenAdvisories_EndForgetsOnlyItsOwnRegistration()
    {
        var first = _advisories.Begin("fo.parking-conflict");
        var second = _advisories.Begin("fo.dropped-call");

        _advisories.End(first); // the older one finishing must not drop the newer
        Assert.Equal("fo.dropped-call", _advisories.CurrentTag);

        _advisories.End(second);
        Assert.Null(_advisories.CurrentTag);
        Assert.False(_advisories.CancelCurrent(out var tag));
        Assert.Null(tag);
    }

    [Fact]
    public void Phrases_AreTheFourCancelWords()
        => Assert.Equal(["disregard", "cancel that", "belay that", "never mind"], Feature().Phrases);
}
