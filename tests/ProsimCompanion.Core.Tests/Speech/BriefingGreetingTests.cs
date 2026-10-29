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
    public void SystemPrompt_QuotesTheExactGreeting_AndNoOtherWord()
    {
        // 2026-10-29: two EGLL briefings at a morning sim clock opened "Good afternoon" — the
        // old prompt's example read 'Good afternoon, Captain.' and the model copied it.
        var prompt = BriefingComposer.SystemPrompt(true, "morning");

        Assert.Contains("exactly 'Good morning, Captain.'", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("afternoon", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("evening", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void SystemPrompt_WithoutAClock_ForbidsAnyGreeting()
    {
        var prompt = BriefingComposer.SystemPrompt(true, null);

        Assert.Contains("Open with 'Captain,'", prompt, StringComparison.Ordinal);
        Assert.Contains("never guess morning, afternoon or evening", prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Good afternoon, Captain. We're departing Heathrow.", "morning", "Good morning, Captain. We're departing Heathrow.")]
    [InlineData("Good evening Captain, departure briefing.", "morning", "Good morning Captain, departure briefing.")]
    [InlineData("Good morning, Captain. Runway two seven right.", "morning", "Good morning, Captain. Runway two seven right.")]
    [InlineData("Good afternoon, Captain. We're departing Heathrow.", null, "Captain. We're departing Heathrow.")]
    [InlineData("Good afternoon. We're departing Heathrow.", null, "Captain, we're departing Heathrow.")]
    [InlineData("Runway two seven right, true heading two seven zero.", "morning", "Runway two seven right, true heading two seven zero.")]
    [InlineData("Surface is dry, good for a rolling start.", "evening", "Surface is dry, good for a rolling start.")]
    public void FixGreeting_PinsTheOpeningToTheSimClock(string sentence, string? timeOfDay, string expected)
        => Assert.Equal(expected, BriefingComposer.FixGreeting(sentence, timeOfDay));
}
