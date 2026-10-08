using ProsimCompanion.Core.TechLog;
using Xunit;

namespace ProsimCompanion.Core.Tests.TechLog;

/// <summary>
/// The tech-log procedural hooks (2026-10-09): what a briefing names, what an arrival
/// briefing filters to, and which open items a tagged checklist line flags.
/// </summary>
public sealed class TechLogConsultationTests
{
    private static TechLogDefect Defect(string title, MelCategory category = MelCategory.C, string? system = null, string? implications = null)
        => new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = title,
            Category = category,
            System = system,
            OperationalImplications = implications ?? "",
            Status = DefectStatus.Deferred,
        };

    [Fact]
    public void DepartureClause_NamesUpToThreeTitles_ThenCountsTheRest()
    {
        Assert.Null(TechLogConsultation.BriefingClause([], departure: true));
        Assert.Equal(
            "Open tech log items: APU inoperative.",
            TechLogConsultation.BriefingClause([Defect("APU inoperative")], departure: true));
        Assert.Equal(
            "Open tech log items: APU inoperative, and Galley oven 2 inoperative.",
            TechLogConsultation.BriefingClause([Defect("APU inoperative"), Defect("Galley oven 2 inoperative")], departure: true));
        Assert.Equal(
            "Open tech log items: A, B, and C, and 2 more.",
            TechLogConsultation.BriefingClause([Defect("A"), Defect("B"), Defect("C"), Defect("D"), Defect("E")], departure: true));
    }

    [Fact]
    public void ArrivalItems_KeepCategoryAAndB_AndLandingSystemsOnly()
    {
        var open = new[]
        {
            Defect("Cabin reading light unserviceable", MelCategory.D),
            Defect("Autobrake LO mode inoperative", MelCategory.C),
            Defect("Left thrust reverser deactivated", MelCategory.C),
            Defect("Nose gear taxi light inoperative", MelCategory.D),
            Defect("Galley oven 2 inoperative", MelCategory.B),
            Defect("Lavatory smoke detector", MelCategory.C, implications: "no operational effect"),
        };

        var arrival = TechLogConsultation.ArrivalItems(open).Select(d => d.Title).ToArray();

        Assert.Equal(
            ["Autobrake LO mode inoperative", "Left thrust reverser deactivated", "Nose gear taxi light inoperative", "Galley oven 2 inoperative"],
            arrival);
        Assert.StartsWith("Open tech log items affecting landing:", TechLogConsultation.BriefingClause(TechLogConsultation.ArrivalItems(open), departure: false));
    }

    [Fact]
    public void RectifiedItems_AreNeverBriefed()
    {
        var fixedItem = Defect("APU inoperative");
        fixedItem.Status = DefectStatus.Rectified;

        Assert.Empty(TechLogConsultation.DepartureItems([fixedItem]));
        Assert.Empty(TechLogConsultation.ArrivalItems([fixedItem]));
    }

    [Fact]
    public void ChecklistLine_MatchesByTag_OrByTitleWordWhenUntagged()
    {
        var open = new[]
        {
            Defect("Number one engine anti-ice valve", system: "anti-ice"),   // tagged
            Defect("APU inoperative"),                                        // untagged: title words
            Defect("Autobrake LO mode inoperative"),                          // untagged: "autobrake" is one word
            Defect("Cabin reading light unserviceable", system: "cabin"),
        };

        Assert.Equal("Number one engine anti-ice valve", Assert.Single(TechLogConsultation.MatchesForChecklistLine("Anti Ice", open)).Title);
        Assert.Equal("APU inoperative", Assert.Single(TechLogConsultation.MatchesForChecklistLine("apu", open)).Title);
        Assert.Equal("Autobrake LO mode inoperative", Assert.Single(TechLogConsultation.MatchesForChecklistLine("autobrake", open)).Title);
        Assert.Empty(TechLogConsultation.MatchesForChecklistLine("brakes", open));   // "autobrake" is not the word "brakes"
        Assert.Empty(TechLogConsultation.MatchesForChecklistLine(null, open));       // an untagged line never flags
        Assert.Empty(TechLogConsultation.MatchesForChecklistLine("  ", open));
    }

    [Fact]
    public void ChecklistNote_IsTheAppendedAdvisory()
    {
        Assert.Equal(" — note, open tech log item: APU inoperative.", TechLogConsultation.ChecklistNote(Defect(" APU inoperative ")));
    }
}
