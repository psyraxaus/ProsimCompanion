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
}
