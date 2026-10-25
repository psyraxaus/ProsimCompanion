using ProsimCompanion.Speech.Recognition;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>Owner request 2026-10-25: the FO may answer "thank you" — but "Thank you." is
/// Whisper's silence hallucination (issue #120), so the reply is gated on the switch, on the
/// FO having just spoken or acted, on acoustic confidence and on the moment not being sterile.</summary>
public sealed class CourtesyReplyCoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 16, 35, 0, TimeSpan.Zero);

    [Fact]
    public void ThankYou_RightAfterTheFoActed_IsAnswered()
    {
        var (decision, reason) = CourtesyReplyCore.Decide(
            "thank you", enabled: true, acousticConfidence: 0.92, Now, Now.AddSeconds(-5), sterile: false);

        Assert.Equal(CourtesyDecision.Reply, decision);
        Assert.Equal("replied", reason);
    }

    [Fact]
    public void SwitchedOff_AbsorbsWithoutAWord()
    {
        var (decision, reason) = CourtesyReplyCore.Decide(
            "thank you", enabled: false, acousticConfidence: 0.92, Now, Now.AddSeconds(-5), sterile: false);

        Assert.Equal(CourtesyDecision.Absorb, decision);
        Assert.Equal("switched-off", reason);
    }

    [Fact]
    public void NothingRecent_TheCruiseBreath_IsAbsorbed()
    {
        var (decision, reason) = CourtesyReplyCore.Decide(
            "thank you", enabled: true, acousticConfidence: 0.95, Now, Now.AddMinutes(-7), sterile: false);

        Assert.Equal(CourtesyDecision.Absorb, decision);
        Assert.Equal("nothing-recent", reason);
    }

    [Fact]
    public void NoActivityEver_IsAbsorbed()
    {
        var (decision, _) = CourtesyReplyCore.Decide("thanks", true, 0.95, Now, null, false);

        Assert.Equal(CourtesyDecision.Absorb, decision);
    }

    [Fact]
    public void LowConfidence_IsAbsorbed_NullConfidenceIsAccepted()
    {
        var low = CourtesyReplyCore.Decide("thank you", true, 0.4, Now, Now.AddSeconds(-3), false);
        var offline = CourtesyReplyCore.Decide("thank you", true, null, Now, Now.AddSeconds(-3), false);

        Assert.Equal((CourtesyDecision.Absorb, "low-confidence"), low);
        Assert.Equal(CourtesyDecision.Reply, offline.Decision);
    }

    [Fact]
    public void Sterile_IsAbsorbed()
    {
        var (decision, reason) = CourtesyReplyCore.Decide("thank you", true, 0.95, Now, Now.AddSeconds(-3), sterile: true);

        Assert.Equal(CourtesyDecision.Absorb, decision);
        Assert.Equal("sterile", reason);
    }

    [Fact]
    public void WindowEdge_TwentySecondsIn_StillAnswered_TwentyOneNot()
    {
        Assert.Equal(CourtesyDecision.Reply,
            CourtesyReplyCore.Decide("cheers", true, 0.9, Now, Now.AddSeconds(-20), false).Decision);
        Assert.Equal(CourtesyDecision.Absorb,
            CourtesyReplyCore.Decide("cheers", true, 0.9, Now, Now.AddSeconds(-21), false).Decision);
    }

    [Theory]
    [InlineData("gear up")]
    [InlineData("thank you for the briefing no questions")]
    [InlineData("")]
    public void NotACourtesy_IsLeftToTheRouter(string normalized)
        => Assert.Equal(CourtesyDecision.NotCourtesy,
            CourtesyReplyCore.Decide(normalized, true, 0.9, Now, Now, false).Decision);

    [Fact]
    public void Replies_CycleThroughThreeWordings()
    {
        var first = CourtesyReplyCore.ReplyText(0);
        Assert.Equal("You're welcome, Captain.", first);
        Assert.NotEqual(first, CourtesyReplyCore.ReplyText(1));
        Assert.NotEqual(CourtesyReplyCore.ReplyText(1), CourtesyReplyCore.ReplyText(2));
        Assert.Equal(first, CourtesyReplyCore.ReplyText(3));
    }
}
