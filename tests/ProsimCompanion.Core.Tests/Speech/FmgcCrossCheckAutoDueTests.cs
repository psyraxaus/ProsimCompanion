using ProsimCompanion.Core.Flight;
using ProsimCompanion.Speech.FoSide;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>The FMGC cross-check's auto trigger belongs to a leg whose plan is in (ADR-0019 §1,
/// issue #221): the arrival's Preflight edge with the flown plan still in the box never fires it.</summary>
public sealed class FmgcCrossCheckAutoDueTests
{
    [Fact]
    public void Armed_AtTheGate_WithInitSettled_IsDue()
        => Assert.True(FmgcCrossCheckService.AutoDue(FlightPhase.Preflight, live: true, "EHAM", "EGLL", v1: 142, doneThisCycle: false, armed: true));

    [Fact]
    public void Disarmed_TheFlownPlanOnTheArrivalsPreflightEdge_IsNotDue()
    {
        // EHAM 2026-11-01 01:50:58Z: "INIT page: EGLL to EHAM" spoken after landing at EHAM.
        Assert.False(FmgcCrossCheckService.AutoDue(FlightPhase.Preflight, live: true, "EGLL", "EHAM", v1: 142, doneThisCycle: false, armed: false));
    }

    [Fact]
    public void TheOldRules_StillHold()
    {
        Assert.False(FmgcCrossCheckService.AutoDue(FlightPhase.Preflight, live: true, "EHAM", "EGLL", v1: 142, doneThisCycle: true));
        Assert.False(FmgcCrossCheckService.AutoDue(FlightPhase.TaxiOut, live: true, "EHAM", "EGLL", v1: 142, doneThisCycle: false));
        Assert.False(FmgcCrossCheckService.AutoDue(FlightPhase.Preflight, live: false, "EHAM", "EGLL", v1: 142, doneThisCycle: false));
        Assert.False(FmgcCrossCheckService.AutoDue(FlightPhase.Preflight, live: true, "EHAM", "EGLL", v1: 0, doneThisCycle: false));
        Assert.False(FmgcCrossCheckService.AutoDue(FlightPhase.Preflight, live: true, "", "EGLL", v1: 142, doneThisCycle: false));
    }
}
