using ProsimCompanion.Speech.Crew;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>Episode cooldown for the parking-conflict advisory (issue #121, 2026-09-05 EGLL):
/// the prep hold published the conflict, released it 16 s later when the reposition remedy
/// started, and the position-select menu republished it — the pilot heard the identical line
/// twice. A clear-then-republish inside the cooldown is one episode; changed guidance (GSX
/// now names a facility) is new information and still speaks.</summary>
public sealed class ParkingConflictAdvisoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 5, 9, 6, 17, TimeSpan.Zero);

    [Fact]
    public void SameText_WithinCooldown_IsARepeat()
        => Assert.True(ParkingConflictAdvisoryService.IsRepeatWithinCooldown(
            "line", T0, "line", T0.AddSeconds(16)));

    [Fact]
    public void SameText_AfterTheCooldown_SpeaksAgain()
        => Assert.False(ParkingConflictAdvisoryService.IsRepeatWithinCooldown(
            "line", T0, "line", T0 + ParkingConflictAdvisoryService.RepeatCooldown));

    [Fact]
    public void ChangedGuidance_IsNeverARepeat()
        // Departure spoke the facility-less form; the arrival conflict names "Parking 14R" —
        // different information, speaks even seconds later.
        => Assert.False(ParkingConflictAdvisoryService.IsRepeatWithinCooldown(
            "facility-less line", T0, "line naming Parking 14R", T0.AddSeconds(5)));

    /// <summary>Issue #141 (2026-10-05 LKPR): when the app cannot make GSX show its menu the
    /// remedy is one toolbar click — not "select the stand, or reposition", and never
    /// "doesn't recognise our parking position" (GSX had not looked yet).</summary>
    [Fact]
    public void MenuUnreachableLine_AsksForTheToolbarClick_AndIsNewGuidance()
    {
        var line = ParkingConflictAdvisoryService.MenuUnreachableAdvisory;

        Assert.Contains("Open the GSX menu once from the toolbar", line);
        Assert.DoesNotContain("recognise", line);
        Assert.DoesNotContain("reposition", line);

        // "Pick the stand" after "open the menu" is different guidance — it still speaks.
        Assert.False(ParkingConflictAdvisoryService.IsRepeatWithinCooldown(
            line, T0, ParkingConflictAdvisoryService.ComposeAdvisory(""), T0.AddSeconds(30)));
    }

    [Fact]
    public void FirstConflictOfTheSession_IsNeverARepeat()
        => Assert.False(ParkingConflictAdvisoryService.IsRepeatWithinCooldown(
            null, null, "line", T0));
}
