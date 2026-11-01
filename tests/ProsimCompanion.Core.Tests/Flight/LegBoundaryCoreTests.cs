using ProsimCompanion.Core.Flight;
using Xunit;

namespace ProsimCompanion.Core.Tests.Flight;

/// <summary>ADR-0019 §2: three steps, order preferred never required, a new plan always
/// begins the leg, the same plan twice does nothing.</summary>
public sealed class LegBoundaryCoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 1, 50, 0, TimeSpan.Zero);

    [Fact]
    public void FirstPlan_BeginsTheLeg_WithoutAWarning()
    {
        var core = new LegBoundaryCore();

        var change = core.PlanLoaded("req:1", T0);

        Assert.NotNull(change);
        Assert.True(change.LegBegan);
        Assert.Null(change.Warning);
        Assert.Null(change.PreviousPlanId);
        Assert.Equal(LegBoundaryState.None, core.Snapshot.State);
        Assert.Equal("req:1", core.Snapshot.PlanId);
    }

    [Fact]
    public void SamePlanTwice_IsOneLeg_NothingHappens()
    {
        var core = new LegBoundaryCore();
        core.PlanLoaded("req:1", T0);

        Assert.Null(core.PlanLoaded("req:1", T0.AddMinutes(5)));
        Assert.Null(core.PlanLoaded(null, T0.AddMinutes(6)));
        Assert.Null(core.PlanLoaded("", T0.AddMinutes(7)));
    }

    [Fact]
    public void TheEhamTurnaround_InOrder_WalksTheThreeStates()
    {
        // EGLL→EHAM flown, deboarded 01:50:03Z, FMS reset by the IOS button, EHAM→EGLL at 03:05:06Z.
        var core = new LegBoundaryCore();
        core.PlanLoaded("req:191949682", T0.AddHours(-3));

        var deboarded = core.Deboarded(T0);
        Assert.NotNull(deboarded);
        Assert.Equal(LegBoundaryState.None, deboarded.From);
        Assert.Equal(LegBoundaryState.Turnaround, deboarded.To);
        Assert.Null(deboarded.Warning);
        Assert.False(deboarded.LegBegan);

        var cleared = core.FmsCleared(T0.AddMinutes(30));
        Assert.NotNull(cleared);
        Assert.Equal(LegBoundaryState.Turnaround, cleared.From);
        Assert.Equal(LegBoundaryState.AwaitingPlan, cleared.To);
        Assert.Null(cleared.Warning);

        var began = core.PlanLoaded("req:191949700", T0.AddMinutes(75));
        Assert.NotNull(began);
        Assert.Equal(LegBoundaryState.AwaitingPlan, began.From);
        Assert.Equal(LegBoundaryState.None, began.To);
        Assert.True(began.LegBegan);
        Assert.Null(began.Warning);
        Assert.Equal("req:191949682", began.PreviousPlanId);
        Assert.False(core.Snapshot.Deboarded);
        Assert.False(core.Snapshot.FmsCleared);
    }

    [Fact]
    public void DuplicateSteps_AreIgnored()
    {
        var core = new LegBoundaryCore();
        core.PlanLoaded("req:1", T0);
        Assert.NotNull(core.Deboarded(T0));
        Assert.Null(core.Deboarded(T0.AddSeconds(1)));
        Assert.NotNull(core.FmsCleared(T0.AddSeconds(2)));
        Assert.Null(core.FmsCleared(T0.AddSeconds(3)));
    }

    [Fact]
    public void FmsClearedFirst_ResetFmsAfterLanding_IsAWarning_NotAnError()
    {
        var core = new LegBoundaryCore();
        core.PlanLoaded("req:1", T0);

        var cleared = core.FmsCleared(T0);
        Assert.NotNull(cleared);
        Assert.Equal(LegBoundaryState.AwaitingPlan, cleared.To);
        Assert.Contains("before deboarding", cleared.Warning);

        var deboarded = core.Deboarded(T0.AddMinutes(10));
        Assert.NotNull(deboarded);
        Assert.Equal(LegBoundaryState.AwaitingPlan, deboarded.From);
        Assert.Equal(LegBoundaryState.AwaitingPlan, deboarded.To);
        Assert.Contains("after the FMS", deboarded.Warning);
        Assert.True(core.Snapshot.Deboarded);
    }

    [Fact]
    public void NewPlanWhileTheFmsStillHoldsTheOldOne_BeginsTheLeg_AndWarns()
    {
        // ADR-0019 §3: the FO's one reminder hangs off this warning.
        var core = new LegBoundaryCore();
        core.PlanLoaded("req:1", T0);
        core.Deboarded(T0.AddMinutes(1));

        var began = core.PlanLoaded("req:2", T0.AddMinutes(20));

        Assert.NotNull(began);
        Assert.True(began.LegBegan);
        Assert.Equal(LegBoundaryState.Turnaround, began.From);
        Assert.Contains("still held the previous plan", began.Warning);
        Assert.Equal(LegBoundaryState.None, core.Snapshot.State);
    }

    [Fact]
    public void NewPlanWithNoDeboarding_StillBeginsTheLeg_AndWarns()
    {
        var core = new LegBoundaryCore();
        core.PlanLoaded("req:1", T0);
        core.FmsCleared(T0.AddMinutes(1));

        var began = core.PlanLoaded("req:2", T0.AddMinutes(2));

        Assert.NotNull(began);
        Assert.True(began.LegBegan);
        Assert.Contains("no deboarding", began.Warning);
    }

    [Fact]
    public void ADifferentPlanAtTheGate_WithNoTurnaround_BeginsTheLeg_AndSaysSo()
    {
        var core = new LegBoundaryCore();
        core.PlanLoaded("req:1", T0);

        var began = core.PlanLoaded("req:2", T0.AddMinutes(2));

        Assert.NotNull(began);
        Assert.True(began.LegBegan);
        Assert.Equal(LegBoundaryState.None, began.From);
        Assert.Contains("replaced req:1", began.Warning);
    }

    [Fact]
    public void Airborne_EndsAnyBoundaryState_AndReArmsTheSteps_KeepingThePlan()
    {
        var core = new LegBoundaryCore();
        core.PlanLoaded("req:1", T0);
        core.Deboarded(T0.AddMinutes(1));
        core.FmsCleared(T0.AddMinutes(2));

        var airborne = core.Airborne(T0.AddHours(1));

        Assert.NotNull(airborne);
        Assert.Equal(LegBoundaryState.AwaitingPlan, airborne.From);
        Assert.Equal(LegBoundaryState.None, airborne.To);
        Assert.Equal("req:1", core.Snapshot.PlanId);
        Assert.False(core.Snapshot.Deboarded);
        // The same plan flown again deboards again (a repositioning hop on one plan).
        Assert.NotNull(core.Deboarded(T0.AddHours(2)));
    }

    [Fact]
    public void Airborne_WithNothingHeld_IsSilent()
    {
        var core = new LegBoundaryCore();
        core.PlanLoaded("req:1", T0);
        Assert.Null(core.Airborne(T0.AddMinutes(30)));
    }

    [Fact]
    public void Restore_AdoptsASnapshot()
    {
        var core = new LegBoundaryCore();
        core.Restore(new LegBoundarySnapshot(LegBoundaryState.Turnaround, "req:9", true, false, T0, "deboarding complete"));

        Assert.Equal(LegBoundaryState.Turnaround, core.Snapshot.State);
        Assert.Null(core.Deboarded(T0.AddMinutes(1)));
        var cleared = core.FmsCleared(T0.AddMinutes(2));
        Assert.NotNull(cleared);
        Assert.Equal(LegBoundaryState.AwaitingPlan, cleared.To);
    }
}
