using ProsimCompanion.Core.Flight;
using ProsimCompanion.Speech.Arbiter;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>Issue #201: while a dialogue holds the mic, the other features' prompts wait.</summary>
public sealed class DialogueHoldRuleTests
{
    private static readonly SpeechContext Ctx = new(FlightPhase.Cruise, 35_000, true);

    [Theory]
    [InlineData("cabin.turbulence.seatbelts", SpeechPriority.Normal, true)]   // the 2026-11-01 signature
    [InlineData("ground.upcall.refuel", SpeechPriority.Normal, true)]
    [InlineData("smalltalk.coffee", SpeechPriority.Low, true)]
    [InlineData("walkaround.announce", SpeechPriority.Low, true)]
    [InlineData("sayintentions.tx", SpeechPriority.Normal, false)]            // ATC is waiting
    [InlineData("fo.answer", SpeechPriority.Normal, false)]                   // the "any questions" answer
    [InlineData("briefing.arrival", SpeechPriority.Normal, false)]
    [InlineData("checklist.read", SpeechPriority.Normal, false)]
    [InlineData("ecam.line", SpeechPriority.Normal, false)]
    [InlineData("clarifier", SpeechPriority.Normal, false)]                  // "did you mean gear down?" — the dialogue's own question
    [InlineData("confirmCommand", SpeechPriority.Normal, false)]
    [InlineData(null, SpeechPriority.Normal, false)]                          // the dialogues speak untagged
    [InlineData("cabin.turbulence.seatbelts", SpeechPriority.High, false)]    // callouts always go
    [InlineData("anything", SpeechPriority.Critical, false)]
    public void Holds_OnlyTaggedNormalAndLowPrompts_OutsideTheExemptFamilies(string? tag, SpeechPriority priority, bool held)
        => Assert.Equal(held, DialogueHoldRule.Holds(new SpeechRequest("x", priority, Tag: tag)));

    [Fact]
    public void Evaluate_DefersWhileBorrowed_AllowsOtherwise()
    {
        var borrowed = false;
        var rule = new DialogueHoldRule(() => borrowed);
        var request = new SpeechRequest("Seat belt signs, captain?", SpeechPriority.Normal, Tag: "cabin.turbulence.seatbelts");

        Assert.Equal(SpeechSuppressionVerdict.Allow, rule.Evaluate(request, Ctx));
        borrowed = true;
        Assert.Equal(SpeechSuppressionVerdict.Defer, rule.Evaluate(request, Ctx));
        Assert.Equal(SpeechSuppressionVerdict.Allow, rule.Evaluate(new SpeechRequest("Climb FL140, Speedbird.", Tag: "sayintentions.tx"), Ctx));
        borrowed = false;
        Assert.Equal(SpeechSuppressionVerdict.Allow, rule.Evaluate(request, Ctx));
    }
}
