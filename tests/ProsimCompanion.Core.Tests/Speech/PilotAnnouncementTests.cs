using ProsimCompanion.Core.Checklists;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Speech.Callouts;
using ProsimCompanion.Speech.Recognition;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>The PF announcement acknowledgements (issue #127) and the "request … checklist"
/// start form — every case here is a phrase the 2026-09-06 EPWA→EGLL leg actually rejected.</summary>
public sealed class PilotAnnouncementTests
{
    [Theory]
    [InlineData("manflex srs runway navblue", FlightPhase.TakeoffRoll)]
    [InlineData("man flex srs runway nav blue", FlightPhase.InitialClimb)]
    [InlineData("man toga srs runway", FlightPhase.TakeoffRoll)]
    public void TakeoffFmaReads_AreAcknowledged(string normalized, FlightPhase phase)
        => Assert.Equal(
            PilotAnnouncementFeature.AnnouncementKind.TakeoffFma,
            PilotAnnouncementFeature.Classify(normalized, phase));

    [Theory]
    [InlineData("glideslope star", FlightPhase.Approach)]
    [InlineData("lockstar", FlightPhase.Approach)]
    [InlineData("loc star", FlightPhase.Descent)]
    [InlineData("localizer star", FlightPhase.Approach)]
    public void CaptureCalls_AreAcknowledged(string normalized, FlightPhase phase)
        => Assert.Equal(
            PilotAnnouncementFeature.AnnouncementKind.CaptureFma,
            PilotAnnouncementFeature.Classify(normalized, phase));

    [Fact]
    public void ManualFlight_IsAcknowledged()
        => Assert.Equal(
            PilotAnnouncementFeature.AnnouncementKind.ManualFlight,
            PilotAnnouncementFeature.Classify("manual flight", FlightPhase.Approach));

    [Fact]
    public void Continue_OnApproach_IsTheMinimumsDecision()
        => Assert.Equal(
            PilotAnnouncementFeature.AnnouncementKind.Continue,
            PilotAnnouncementFeature.Classify("continue", FlightPhase.Approach));

    [Theory]
    [InlineData("continue", FlightPhase.Cruise)]
    [InlineData("continue", FlightPhase.Preflight)]
    [InlineData("glideslope star", FlightPhase.Cruise)]
    [InlineData("manflex srs runway navblue", FlightPhase.Cruise)]
    [InlineData("set qnh", FlightPhase.Approach)]
    public void OutOfPhaseOrUnrelated_FallsThrough(string normalized, FlightPhase phase)
        // The words keep their ordinary meanings elsewhere — a null lets normal routing decide.
        => Assert.Null(PilotAnnouncementFeature.Classify(normalized, phase));

    [Fact]
    public void ChecklistStartPhrases_IncludeTheRequestForm()
    {
        // "Request taxi checklist" was rejected on 2026-09-06 and the pilot re-phrased twice.
        var definition = new ChecklistDefinition { Checklist = "Taxi" };
        Assert.Contains("request Taxi checklist", UtteranceRouter.StartPhrases(definition));
        Assert.Contains("Taxi checklist", UtteranceRouter.StartPhrases(definition));
    }
}
