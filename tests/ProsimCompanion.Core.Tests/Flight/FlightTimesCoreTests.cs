using ProsimCompanion.Core.Flight;
using Xunit;

namespace ProsimCompanion.Core.Tests.Flight;

public sealed class FlightTimesCoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 23, 10, 45, 0, TimeSpan.Zero);

    [Fact]
    public void FullLeg_StampsEachEdgeOnce()
    {
        var core = new FlightTimesCore();

        core.Apply(FlightPhase.Departure, FlightPhase.PushbackAndStart, T0);
        core.Apply(FlightPhase.PushbackAndStart, FlightPhase.TaxiOut, T0.AddMinutes(5));
        core.Apply(FlightPhase.TaxiOut, FlightPhase.TakeoffRoll, T0.AddMinutes(15));
        core.Apply(FlightPhase.TakeoffRoll, FlightPhase.InitialClimb, T0.AddMinutes(16));
        core.Apply(FlightPhase.InitialClimb, FlightPhase.Climb, T0.AddMinutes(18));
        // A phase-engine wobble must not move the takeoff stamp.
        core.Apply(FlightPhase.Cruise, FlightPhase.Climb, T0.AddMinutes(60));
        core.Apply(FlightPhase.Approach, FlightPhase.LandingRollout, T0.AddMinutes(140));
        var times = core.Apply(FlightPhase.TaxiIn, FlightPhase.Shutdown, T0.AddMinutes(150));

        Assert.Equal(T0, times.OffBlocksUtc);
        Assert.Equal(T0.AddMinutes(16), times.TakeoffUtc);
        Assert.Equal(T0.AddMinutes(140), times.LandingUtc);
        Assert.Equal(T0.AddMinutes(150), times.OnBlocksUtc);
        Assert.Equal(TimeSpan.FromMinutes(150), times.BlockTime(T0.AddHours(9)));
        Assert.Equal(TimeSpan.FromMinutes(124), times.FlightTime(T0.AddHours(9)));
    }

    // ---- Ticket t-20261010-0726: the GSX arrival reset fires on the Shutdown edge ----

    [Fact]
    public void CycleReset_DuringTheArrival_KeepsTheStamps()
    {
        var core = new FlightTimesCore();
        core.Apply(FlightPhase.Departure, FlightPhase.PushbackAndStart, T0, 9788);
        core.Apply(FlightPhase.TakeoffRoll, FlightPhase.InitialClimb, T0.AddMinutes(16), 9522);
        core.Apply(FlightPhase.Approach, FlightPhase.LandingRollout, T0.AddMinutes(178), 4074);
        core.Apply(FlightPhase.LandingRollout, FlightPhase.TaxiIn, T0.AddMinutes(179));

        // 07:15:22.396Z: GsxAutomationService raised FlightCycleReset 2 ms before the Shutdown stamp.
        Assert.False(core.ResetForNewCycle());
        var times = core.Apply(FlightPhase.TaxiIn, FlightPhase.Shutdown, T0.AddMinutes(183), 4003);

        Assert.Equal(T0, times.OffBlocksUtc);
        Assert.Equal(T0.AddMinutes(16), times.TakeoffUtc);
        Assert.Equal(T0.AddMinutes(178), times.LandingUtc);
        Assert.Equal(T0.AddMinutes(183), times.OnBlocksUtc);
        Assert.Equal(9522 - 4003, times.FuelUsedKg);
    }

    [Fact]
    public void CycleReset_AtShutdown_KeepsTheStamps_UntilTheNextLegEdge()
    {
        var core = new FlightTimesCore();
        core.Apply(FlightPhase.Departure, FlightPhase.TaxiOut, T0);
        core.Apply(FlightPhase.TaxiIn, FlightPhase.Shutdown, T0.AddMinutes(150));

        Assert.False(core.ResetForNewCycle());
        Assert.Equal(T0.AddMinutes(150), core.Current.OnBlocksUtc);

        var next = core.Apply(FlightPhase.Shutdown, FlightPhase.Preflight, T0.AddMinutes(200));
        Assert.Equal(FlightTimesSnapshot.Empty, next);
    }

    [Fact]
    public void CycleReset_AtTheGateBeforeTheLeg_Clears()
    {
        var core = new FlightTimesCore();
        core.Apply(FlightPhase.ColdAndDark, FlightPhase.Preflight, T0);
        core.Apply(FlightPhase.Preflight, FlightPhase.Departure, T0.AddMinutes(10));

        Assert.True(core.ResetForNewCycle());
        Assert.Equal(FlightTimesSnapshot.Empty, core.Current);
    }

    [Fact]
    public void TurnaroundStart_StraightFromShutdown_BeginsANewLeg()
    {
        var core = new FlightTimesCore();
        core.Apply(FlightPhase.Departure, FlightPhase.TaxiOut, T0);
        core.Apply(FlightPhase.TaxiIn, FlightPhase.Shutdown, T0.AddMinutes(150));

        var next = core.Apply(FlightPhase.Shutdown, FlightPhase.PushbackAndStart, T0.AddMinutes(200));

        Assert.Equal(T0.AddMinutes(200), next.OffBlocksUtc);
        Assert.Null(next.OnBlocksUtc);
    }

    [Fact]
    public void BlockTime_RunsWhileOnTheLeg()
    {
        var core = new FlightTimesCore();
        var times = core.Apply(FlightPhase.Departure, FlightPhase.TaxiOut, T0);

        Assert.Equal(TimeSpan.FromMinutes(30), times.BlockTime(T0.AddMinutes(30)));
        Assert.Null(times.FlightTime(T0.AddMinutes(30)));
    }

    /// <summary>Issue #167 (2026-10-10, restart in the climb): the first commit Unknown → Cruise
    /// used to seed takeoff = now with the fuel of that moment — "0.7 t used" for a 5.2 t leg.
    /// Now the takeoff is recorded as unknown and nothing is invented.</summary>
    [Fact]
    public void AppStartedAirborne_LeavesTheTakeoffUnknown_NeverSeedsNow()
    {
        var core = new FlightTimesCore();
        var times = core.Apply(FlightPhase.Unknown, FlightPhase.Cruise, T0, 7976);

        Assert.True(times.TakeoffUnknown);
        Assert.Null(times.TakeoffUtc);
        Assert.Null(times.TakeoffFobKg);
        Assert.Null(times.OffBlocksUtc);
        Assert.Null(times.FuelUsedKg);

        // The rest of the leg still stamps; the flag stays so the debrief can say why.
        var landed = core.Apply(FlightPhase.Approach, FlightPhase.LandingRollout, T0.AddHours(2), 4433);
        Assert.Equal(T0.AddHours(2), landed.LandingUtc);
        Assert.True(landed.TakeoffUnknown);
        Assert.Null(landed.FuelUsedKg);
    }

    [Fact]
    public void AppStartedOnTheRunway_StillStampsTheRealTakeoffEdge()
    {
        var core = new FlightTimesCore();
        core.Apply(FlightPhase.Unknown, FlightPhase.TakeoffRoll, T0);
        var times = core.Apply(FlightPhase.TakeoffRoll, FlightPhase.InitialClimb, T0.AddMinutes(1), 9580);

        Assert.Equal(T0.AddMinutes(1), times.TakeoffUtc);
        Assert.Equal(T0.AddMinutes(1), times.OffBlocksUtc); // backfilled so block ≥ flight time
        Assert.False(times.TakeoffUnknown);
    }

    // ---- Issue #167: stamps restored from the flight-state snapshot ----

    private static FlightTimesSnapshot Airborne() => new(T0, T0.AddMinutes(14), null, null, 9879, 9580);

    [Fact]
    public void Restore_BeforeTheFirstCommit_IsJudgedByThatCommit()
    {
        var core = new FlightTimesCore();
        Assert.Equal(FlightTimesSnapshot.Empty, core.Restore(Airborne(), T0.AddHours(1)));

        var times = core.Apply(FlightPhase.Unknown, FlightPhase.Cruise, T0.AddHours(1), 7976);

        Assert.Equal(T0.AddMinutes(14), times.TakeoffUtc);
        Assert.Equal(9580, times.TakeoffFobKg);
        Assert.Equal(T0, times.OffBlocksUtc);
        Assert.False(times.TakeoffUnknown);

        var onBlocks = core.Apply(FlightPhase.TaxiIn, FlightPhase.Shutdown, T0.AddHours(3), 4351);
        Assert.Equal(9580 - 4351, onBlocks.FuelUsedKg);
    }

    [Fact]
    public void Restore_AfterTheFirstCommit_IsJudgedAtOnce()
    {
        var core = new FlightTimesCore();
        core.Apply(FlightPhase.Unknown, FlightPhase.Descent, T0.AddHours(2));
        Assert.True(core.Current.TakeoffUnknown);

        var times = core.Restore(Airborne(), T0.AddHours(2));

        Assert.Equal(T0.AddMinutes(14), times.TakeoffUtc);
        Assert.False(times.TakeoffUnknown);
    }

    [Fact]
    public void Accept_InTheAir_KeepsOffBlocksAndTakeoff_RefusesALandedLeg()
    {
        var landed = Airborne() with { LandingUtc = T0.AddHours(2), LandingFobKg = 4433 };

        Assert.Equal(Airborne(), FlightTimesCore.Accept(Airborne(), FlightPhase.Cruise, T0.AddHours(1)));
        Assert.Equal(FlightTimesSnapshot.Empty, FlightTimesCore.Accept(landed, FlightPhase.Cruise, T0.AddHours(3)));
    }

    [Fact]
    public void Accept_TaxiingIn_KeepsTheLanding_Shutdown_KeepsARecentOnBlocks()
    {
        var landed = Airborne() with { LandingUtc = T0.AddHours(2), LandingFobKg = 4433 };
        var onBlocks = landed with { OnBlocksUtc = T0.AddHours(2.2), OnBlocksFobKg = 4351 };

        Assert.Equal(landed, FlightTimesCore.Accept(landed, FlightPhase.TaxiIn, T0.AddHours(2.1)));
        // An on-blocks stamp while still taxiing in belongs to an older leg: nothing comes back.
        Assert.Equal(FlightTimesSnapshot.Empty, FlightTimesCore.Accept(onBlocks, FlightPhase.TaxiIn, T0.AddHours(2.1)));
        Assert.Equal(onBlocks, FlightTimesCore.Accept(onBlocks, FlightPhase.Shutdown, T0.AddHours(2.5)));
        Assert.Equal(FlightTimesSnapshot.Empty, FlightTimesCore.Accept(onBlocks, FlightPhase.Shutdown, T0.AddHours(9)));
    }

    [Fact]
    public void Accept_AtTheGate_KeepsNothing_PushingKeepsOffBlocksOnly()
    {
        var pushing = new FlightTimesSnapshot(T0, null, null, null, 9879);

        Assert.Equal(FlightTimesSnapshot.Empty, FlightTimesCore.Accept(Airborne(), FlightPhase.Preflight, T0.AddHours(5)));
        Assert.Equal(FlightTimesSnapshot.Empty, FlightTimesCore.Accept(Airborne(), FlightPhase.ColdAndDark, T0.AddHours(5)));
        Assert.Equal(pushing, FlightTimesCore.Accept(pushing, FlightPhase.TaxiOut, T0.AddMinutes(3)));
        // A takeoff stamp while still taxiing out is an older leg's: nothing comes back.
        Assert.Equal(FlightTimesSnapshot.Empty, FlightTimesCore.Accept(Airborne(), FlightPhase.TaxiOut, T0.AddMinutes(3)));
    }

    [Fact]
    public void Restore_WithATakeoffUnknownSnapshot_KeepsTheFlag()
    {
        var core = new FlightTimesCore();
        core.Restore(new FlightTimesSnapshot(null, null, null, null, TakeoffUnknown: true), T0);
        var times = core.Apply(FlightPhase.Unknown, FlightPhase.Cruise, T0);

        Assert.True(times.TakeoffUnknown);
        Assert.Null(times.TakeoffUtc);
    }

    [Fact]
    public void NextLeg_ClearsTheStamps()
    {
        var core = new FlightTimesCore();
        core.Apply(FlightPhase.Departure, FlightPhase.TaxiOut, T0);
        core.Apply(FlightPhase.TaxiIn, FlightPhase.Shutdown, T0.AddHours(2));

        var times = core.Apply(FlightPhase.Shutdown, FlightPhase.Preflight, T0.AddHours(3));

        Assert.Equal(FlightTimesSnapshot.Empty, times);
    }

    [Fact]
    public void ExplicitReset_Clears()
    {
        var core = new FlightTimesCore();
        core.Apply(FlightPhase.Departure, FlightPhase.TaxiOut, T0);

        core.Reset();

        Assert.Equal(FlightTimesSnapshot.Empty, core.Current);
    }

    /// <summary>Issue #155: the fuel on board rides with each stamp; a missing figure leaves the
    /// stamp alone; "used" is takeoff to on-blocks (landing while still taxiing in).</summary>
    [Fact]
    public void Apply_StampsTheFuelWithEachTime()
    {
        var core = new FlightTimesCore();
        core.Apply(FlightPhase.Departure, FlightPhase.PushbackAndStart, T0, 9576);
        core.Apply(FlightPhase.TakeoffRoll, FlightPhase.InitialClimb, T0.AddMinutes(16), 9350);
        var landed = core.Apply(FlightPhase.Approach, FlightPhase.LandingRollout, T0.AddMinutes(140), 4530);

        Assert.Equal(9576, landed.OffBlocksFobKg);
        Assert.Equal(9350, landed.TakeoffFobKg);
        Assert.Equal(4530, landed.LandingFobKg);
        Assert.Null(landed.OnBlocksFobKg);
        Assert.Equal(4820, landed.FuelUsedKg);

        var onBlocks = core.Apply(FlightPhase.TaxiIn, FlightPhase.Shutdown, T0.AddMinutes(150), 4410);
        Assert.Equal(4410, onBlocks.OnBlocksFobKg);
        Assert.Equal(4940, onBlocks.FuelUsedKg);
    }

    [Fact]
    public void Apply_WithoutAFuelFigure_StillStampsTheTime()
    {
        var core = new FlightTimesCore();
        var times = core.Apply(FlightPhase.TakeoffRoll, FlightPhase.InitialClimb, T0, null);

        Assert.Equal(T0, times.TakeoffUtc);
        Assert.Null(times.TakeoffFobKg);
        Assert.Null(times.FuelUsedKg);
    }
}
