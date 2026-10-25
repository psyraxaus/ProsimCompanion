using ProsimCompanion.Speech.Briefings;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>Owner report 2026-10-25: "good morning" at sim 16:35. The briefing now carries the
/// sim's local time of day as a word, the template opens with it, and the model is told to
/// greet by it or not at all.</summary>
public sealed class BriefingGreetingTests
{
    private static BriefingFacts Facts(string? timeOfDay)
        => new(true, "EKCH", "22R", "KEMA5C", null, null, NavDataFacts.None,
            null, null, null, null, null, null, null, TimeOfDay: timeOfDay);

    [Fact]
    public void FactBlock_CarriesTheWord_NotAClockFigure()
    {
        var block = BriefingComposer.FactBlock(Facts("afternoon"));

        Assert.Contains("- Time of day at the aircraft: afternoon", block, StringComparison.Ordinal);
        Assert.DoesNotContain("16:35", block, StringComparison.Ordinal);
    }

    [Fact]
    public void FactBlock_OmitsTheLine_WhenTheClockIsUnknown()
        => Assert.DoesNotContain("Time of day", BriefingComposer.FactBlock(Facts(null)), StringComparison.Ordinal);

    [Fact]
    public void TemplateOpening_GreetsByTheWord_OrStaysBare()
    {
        var greeted = BriefingComposer.Sections(Facts("evening"));
        var bare = BriefingComposer.Sections(Facts(null));

        Assert.Equal("Good evening, Captain. Departure briefing.", greeted[0].Text);
        Assert.Equal("Departure briefing.", bare[0].Text);
    }

    [Fact]
    public void SystemPrompt_TellsTheModelToGreetByTheFact_AndNeverGuess()
    {
        var prompt = BriefingComposer.SystemPrompt(true);

        Assert.Contains("Time of day at the aircraft", prompt, StringComparison.Ordinal);
        Assert.Contains("never guess morning or evening", prompt, StringComparison.Ordinal);
    }
}
