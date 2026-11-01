using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Gsx.Automation;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

/// <summary>"A new plan is a new leg" (EHAM 2026-11-01): when the departure side resets on a
/// new SimBrief plan, and when it must not.</summary>
public sealed class NewPlanCycleResetTests
{
    private static FlightDataSnapshot Parked() => new() { IsValid = true, OnGround = true, AnyEngineRunning = false, GroundSpeedKt = 0 };

    private static OfpData Plan(string request, string origin = "EHAM", string destination = "EGLL")
        => new() { RequestId = request, OriginIcao = origin, DestinationIcao = destination };

    [Fact]
    public void PlanId_IsTheRequest_ElseTheRoute_ElseNothing()
    {
        Assert.Equal("req:191949682", NewPlanCycleReset.PlanId(Plan("191949682")));
        Assert.Equal("route:EHAM-EGLL", NewPlanCycleReset.PlanId(new OfpData { OriginIcao = "eham", DestinationIcao = "egll" }));
        Assert.Null(NewPlanCycleReset.PlanId(new OfpData()));
        Assert.Null(NewPlanCycleReset.PlanId(null));
    }

    [Fact]
    public void ANewPlan_AtTheStand_ResetsAClosedCycle()
    {
        // The signature: cycle closed at 14:03:54 with nothing called, leg two's plan at 14:05:06.
        Assert.True(NewPlanCycleReset.ShouldReset(Plan("191949682"), "req:191935630", cycleComplete: true, boardingObserved: false, Parked()));
    }

    [Fact]
    public void ANewPlan_BeforeAnyService_ResetsAnOpenCycle_ButNotOneUnderWay()
    {
        Assert.True(NewPlanCycleReset.ShouldReset(Plan("B"), "req:A", cycleComplete: false, boardingObserved: false, Parked()));
        // Boarding already running for the plan the cycle was opened with: a late re-import must not reset it.
        Assert.False(NewPlanCycleReset.ShouldReset(Plan("B"), "req:A", cycleComplete: false, boardingObserved: true, Parked()));
    }

    [Fact]
    public void RefuelOrCateringAlreadyRun_DoNotHoldTheOldLeg()
    {
        // ADR-0019 §6 (issue #213): only BOARDING holds the old leg. The caller passes the
        // boarding-only observation, so a cycle with refuel and catering done but no boarding
        // reads boardingObserved=false and resets — those services are re-called for the new leg.
        Assert.True(NewPlanCycleReset.ShouldReset(Plan("B"), "req:A", cycleComplete: false, boardingObserved: false, Parked()));
    }

    [Fact]
    public void TheSamePlan_NeverResetsTwice()
    {
        Assert.False(NewPlanCycleReset.ShouldReset(Plan("A"), "req:A", cycleComplete: true, boardingObserved: false, Parked()));
        Assert.False(NewPlanCycleReset.ShouldReset(null, null, cycleComplete: true, boardingObserved: false, Parked()));
    }

    [Fact]
    public void AwayFromTheStand_NothingResets()
    {
        var plan = Plan("B");
        Assert.False(NewPlanCycleReset.ShouldReset(plan, "req:A", true, false, Parked() with { AnyEngineRunning = true }));
        Assert.False(NewPlanCycleReset.ShouldReset(plan, "req:A", true, false, Parked() with { GroundSpeedKt = 8 }));
        Assert.False(NewPlanCycleReset.ShouldReset(plan, "req:A", true, false, Parked() with { OnGround = false }));
        Assert.False(NewPlanCycleReset.ShouldReset(plan, "req:A", true, false, Parked() with { IsValid = false }));
        Assert.False(NewPlanCycleReset.ShouldReset(plan, "req:A", true, false, null));
    }
}
