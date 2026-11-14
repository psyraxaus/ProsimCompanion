using ProsimCompanion.Core.Checklists;
using ProsimCompanion.Speech.Checklists;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>Issue #205, EGLL→LOWW 2026-11-14: on the Descent checklist's "Landing Elevation"
/// line the pilot read the elevation, the line accepted only "set" / "auto", and the FCU's
/// "Say again — which field?" answered instead — three runs in a row. A value read back on a
/// line that declares "set" or "checked" is that line's answer.</summary>
public sealed class ChecklistValueAnswerTests
{
    /// <summary>The shipped descent.json lines, as loaded.</summary>
    private static readonly ChecklistItemDefinition LandingElevation = new()
    {
        Say = "Landing Elevation", ExpectedResponse = "SET", AcceptedPhrases = ["set", "auto"],
    };

    private static readonly ChecklistItemDefinition EcamStatus = new()
    {
        Say = "ECAM Status", ExpectedResponse = "CHECKED", AcceptedPhrases = ["checked", "check"],
    };

    private static readonly ChecklistItemDefinition DescentBriefing = new()
    {
        Say = "Descent Briefing", ExpectedResponse = "COMPLETED", AcceptedPhrases = ["complete", "completed", "confirmed"],
    };

    [Theory]
    [InlineData("Eight.")]                              // voice.unmatched 06:18:38Z, reason not-an-answer
    [InlineData("Six hundred feet.")]
    [InlineData("600 feet.")]
    [InlineData("six zero zero")]
    [InlineData("Five hundred and ninety eight feet")]
    [InlineData("Landing elevation, six hundred feet.")] // the challenge said back before the value
    [InlineData("minus eleven feet")]
    [InlineData("Six hundred, set.")]                    // already accepted: "set" as a whole word
    [InlineData("set")]
    public void LandingElevation_TakesTheValueReadBack(string answer)
        => Assert.True(SpokenChecklistEngine.IsAccepted(LandingElevation, answer));

    [Theory]
    [InlineData("Descend flight level one seven zero")]  // said to ATC while the line waits
    [InlineData("turn left heading two two zero")]
    [InlineData("umm what was that noise")]
    [InlineData("feet")]                                 // a unit without a number
    [InlineData("elevation")]
    [InlineData("")]
    public void LandingElevation_DoesNotTakeOtherSpeech_ForItsAnswer(string heard)
        => Assert.False(SpokenChecklistEngine.IsAccepted(LandingElevation, heard));

    [Fact]
    public void TheRuleReadsTheLinesOwnAcceptedPhrases_NotItsName()
    {
        // "checked" declared: a value line too. Only the universal "checked": not one.
        Assert.True(ChecklistValueAnswer.LineTakesAValue(LandingElevation));
        Assert.True(ChecklistValueAnswer.LineTakesAValue(EcamStatus));
        Assert.False(ChecklistValueAnswer.LineTakesAValue(DescentBriefing));
        Assert.False(SpokenChecklistEngine.IsAccepted(DescentBriefing, "Six hundred feet."));
        Assert.True(SpokenChecklistEngine.IsAccepted(DescentBriefing, "checked"));

        // The same line under any other name, in a pilot's own checklist file.
        var renamed = new ChecklistItemDefinition { Say = "Field height", AcceptedPhrases = ["Set"] };
        Assert.True(SpokenChecklistEngine.IsAccepted(renamed, "Six hundred feet."));
    }

    [Fact]
    public void LinesWithTheirOwnParsing_KeepIt()
    {
        // A flap-config line: digits there name a configuration, the engine's own read-back judges them.
        var flaps = new ChecklistItemDefinition { Say = "Flaps", Expects = "flapConfig", AcceptedPhrases = ["set"] };
        Assert.False(ChecklistValueAnswer.LineTakesAValue(flaps));

        // A number line accepted any number before, and still nothing but its phrases otherwise.
        var qnh = new ChecklistItemDefinition { Say = "Baro", Expects = "number", AcceptedPhrases = ["set"] };
        Assert.False(ChecklistValueAnswer.LineTakesAValue(qnh));
        Assert.True(SpokenChecklistEngine.IsAccepted(qnh, "one zero one seven"));
        Assert.False(SpokenChecklistEngine.IsAccepted(qnh, "checked"));
    }
}
