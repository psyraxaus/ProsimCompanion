using ProsimCompanion.Core.Flight;
using ProsimCompanion.Gsx.Sync;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

/// <summary>The FOB-restore write-safety gate (issue #59): at the 2026-08-16 flight test a
/// bogus startup phase classification let the restore overwrite 9576 kg of loaded fuel with
/// 3344 kg saved by a previous session. After an in-session arrival the restore runs freely;
/// at startup (issue #124, 2026-09-05: the saved landing fuel was never applied) it runs only
/// with a SAVED value, the flight-live gate up, and an at-the-stand phase.</summary>
public sealed class GsxFobRestoreGateTests
{
    [Fact]
    public void AtStartup_WithSavedValueAndFlightLiveAtTheStand_Restores()
        => Assert.Equal(
            GsxArrivalService.FobRestoreDecision.RestoreAtStartup,
            GsxArrivalService.DecideFobRestore(
                alreadyRestored: false, saveLoadFobEnabled: true, planImported: false,
                airborneThisSession: false, flightLive: true, phase: FlightPhase.ColdAndDark,
                savedValueExists: true));

    [Fact]
    public void AtStartup_WithoutASavedValue_NeverAppliesTheDefault()
        // The configured reset default at startup is exactly the #59 clobber — only a value
        // this app saved after a real arrival may be applied to a fresh session.
        => Assert.Equal(
            GsxArrivalService.FobRestoreDecision.NotSafeAtStartup,
            GsxArrivalService.DecideFobRestore(
                alreadyRestored: false, saveLoadFobEnabled: true, planImported: false,
                airborneThisSession: false, flightLive: true, phase: FlightPhase.Preflight,
                savedValueExists: false));

    [Fact]
    public void AtStartup_FlightNotLive_Holds()
        // ProSim pushes plausible-looking data with MSFS on the menu (#114) — a restore
        // before the flight-live gate opens writes into a session that does not exist yet.
        => Assert.Equal(
            GsxArrivalService.FobRestoreDecision.NotSafeAtStartup,
            GsxArrivalService.DecideFobRestore(
                alreadyRestored: false, saveLoadFobEnabled: true, planImported: false,
                airborneThisSession: false, flightLive: false, phase: FlightPhase.ColdAndDark,
                savedValueExists: true));

    [Theory]
    [InlineData(FlightPhase.Unknown)]
    [InlineData(FlightPhase.Cruise)]
    [InlineData(FlightPhase.TaxiIn)]
    public void AtStartup_AwayFromTheStand_Holds(FlightPhase phase)
        // A mid-flight restart classifies straight into an airborne/taxi phase — restoring
        // ground fuel there is the #59 disaster in a new costume.
        => Assert.Equal(
            GsxArrivalService.FobRestoreDecision.NotSafeAtStartup,
            GsxArrivalService.DecideFobRestore(
                alreadyRestored: false, saveLoadFobEnabled: true, planImported: false,
                airborneThisSession: false, flightLive: true, phase: phase,
                savedValueExists: true));

    [Fact]
    public void AfterAnArrivalThisSession_Restores()
        => Assert.Equal(
            GsxArrivalService.FobRestoreDecision.Restore,
            GsxArrivalService.DecideFobRestore(
                alreadyRestored: false, saveLoadFobEnabled: true, planImported: false,
                airborneThisSession: true));

    [Fact]
    public void RestoresAtMostOnce()
        => Assert.Equal(
            GsxArrivalService.FobRestoreDecision.AlreadyRestored,
            GsxArrivalService.DecideFobRestore(
                alreadyRestored: true, saveLoadFobEnabled: true, planImported: false,
                airborneThisSession: true));

    [Fact]
    public void DisabledOption_NeverRestores()
        => Assert.Equal(
            GsxArrivalService.FobRestoreDecision.Disabled,
            GsxArrivalService.DecideFobRestore(
                alreadyRestored: false, saveLoadFobEnabled: false, planImported: false,
                airborneThisSession: true));

    [Fact]
    public void ImportedFlightPlan_BlocksTheRestore()
        // Predecessor guard: once an OFP is imported the planned fuel is authoritative — a
        // mid-turnaround restart must never clobber it. Applies to the startup path too.
        => Assert.Equal(
            GsxArrivalService.FobRestoreDecision.PlanImported,
            GsxArrivalService.DecideFobRestore(
                alreadyRestored: false, saveLoadFobEnabled: true, planImported: true,
                airborneThisSession: false, flightLive: true, phase: FlightPhase.Preflight,
                savedValueExists: true));
}
