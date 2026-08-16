using ProsimCompanion.Gsx.Sync;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

/// <summary>The FOB-restore write-safety gate (issue #59): at the 2026-08-16 flight test a
/// bogus startup phase classification let the restore overwrite 9576 kg of loaded fuel with
/// 3344 kg saved by a previous session. The restore may only ever run after the aircraft has
/// been airborne in THIS session.</summary>
public sealed class GsxFobRestoreGateTests
{
    [Fact]
    public void AtStartup_NeverRestores()
        => Assert.Equal(
            GsxArrivalService.FobRestoreDecision.NoArrivalThisSession,
            GsxArrivalService.DecideFobRestore(
                alreadyRestored: false, saveLoadFobEnabled: true, planImported: false, airborneThisSession: false));

    [Fact]
    public void AfterAnArrivalThisSession_Restores()
        => Assert.Equal(
            GsxArrivalService.FobRestoreDecision.Restore,
            GsxArrivalService.DecideFobRestore(
                alreadyRestored: false, saveLoadFobEnabled: true, planImported: false, airborneThisSession: true));

    [Fact]
    public void RestoresAtMostOnce()
        => Assert.Equal(
            GsxArrivalService.FobRestoreDecision.AlreadyRestored,
            GsxArrivalService.DecideFobRestore(
                alreadyRestored: true, saveLoadFobEnabled: true, planImported: false, airborneThisSession: true));

    [Fact]
    public void DisabledOption_NeverRestores()
        => Assert.Equal(
            GsxArrivalService.FobRestoreDecision.Disabled,
            GsxArrivalService.DecideFobRestore(
                alreadyRestored: false, saveLoadFobEnabled: false, planImported: false, airborneThisSession: true));

    [Fact]
    public void ImportedFlightPlan_BlocksTheRestore()
        // Predecessor guard: once an OFP is imported the planned fuel is authoritative — a
        // mid-turnaround restart must never clobber it.
        => Assert.Equal(
            GsxArrivalService.FobRestoreDecision.PlanImported,
            GsxArrivalService.DecideFobRestore(
                alreadyRestored: false, saveLoadFobEnabled: true, planImported: true, airborneThisSession: true));
}
