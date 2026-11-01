using ProsimCompanion.Core.Flight;
using ProsimCompanion.Speech.FoSide;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>ADR-0019 §3 (issue #215): one reminder when the new plan lands while the FMS
/// still holds the flown pair — never when the FMS was cleared first, never when the box
/// already reads the new route.</summary>
public sealed class FmsPlanReminderTests
{
    [Fact]
    public void PlanInTurnaround_FmsStillTheFlownPair_Reminds()
    {
        Assert.True(FmsPlanReminderService.ShouldRemind(LegBoundaryState.Turnaround, "EGLL", "EHAM", "EHAM", "EGLL"));
    }

    [Fact]
    public void FmsAlreadyReadsTheNewRoute_NoReminder()
    {
        Assert.False(FmsPlanReminderService.ShouldRemind(LegBoundaryState.Turnaround, "EHAM", "EGLL", "EHAM", "EGLL"));
        Assert.False(FmsPlanReminderService.ShouldRemind(LegBoundaryState.Turnaround, "eham", " EGLL ", "EHAM", "EGLL"));
    }

    [Theory]
    [InlineData(LegBoundaryState.AwaitingPlan)]
    [InlineData(LegBoundaryState.None)]
    public void FmsClearedFirst_OrNoTurnaround_NoReminder(LegBoundaryState from)
    {
        Assert.False(FmsPlanReminderService.ShouldRemind(from, "EGLL", "EHAM", "EHAM", "EGLL"));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("----", "----")]
    [InlineData("EGLL", null)]
    public void EmptyOrUnreadFms_NoReminder(string? origin, string? destination)
    {
        Assert.False(FmsPlanReminderService.ShouldRemind(LegBoundaryState.Turnaround, origin, destination, "EHAM", "EGLL"));
    }
}
