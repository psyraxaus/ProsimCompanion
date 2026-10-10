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

    [Fact]
    public void AppStartedAirborne_BackfillsOffBlocks()
    {
        var core = new FlightTimesCore();
        var times = core.Apply(FlightPhase.Unknown, FlightPhase.Cruise, T0);

        Assert.Equal(T0, times.TakeoffUtc);
        Assert.Equal(T0, times.OffBlocksUtc);
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
