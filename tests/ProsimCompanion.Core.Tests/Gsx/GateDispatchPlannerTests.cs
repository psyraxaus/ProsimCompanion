using ProsimCompanion.Core.Flight;
using ProsimCompanion.Gsx.Gate;
using ProsimCompanion.Gsx.Mirror;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

/// <summary>The arrival-gate dispatch decision (2026-09-21): pick the airport in the air,
/// send while rolling, never once parked.</summary>
public sealed class GateDispatchPlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 10, 0, 0, TimeSpan.Zero);

    private static FlightDataSnapshot Air() => new() { IsValid = true, OnGround = false, GroundSpeedKt = 440, AnyEngineRunning = true };
    private static FlightDataSnapshot Taxi() => new() { IsValid = true, OnGround = true, GroundSpeedKt = 15, AnyEngineRunning = true };
    private static FlightDataSnapshot Parked() => new() { IsValid = true, OnGround = true, GroundSpeedKt = 0, AnyEngineRunning = false };

    private static GateDispatchInputs Inputs(
        bool ready = true, string? loaded = "LIRF", string? destination = "EGLL",
        FlightPhase phase = FlightPhase.Cruise, FlightDataSnapshot? data = null,
        DateTimeOffset? lastPick = null, int attempts = 0, DateTimeOffset? lastWalk = null)
        => new(ready, loaded, destination, phase, data ?? Air(), Now, lastPick, attempts, lastWalk);

    [Fact]
    public void NotReady_OrNoDestination_Waits()
    {
        Assert.Equal(GateDispatchStep.Wait, GateDispatchPlanner.Decide(Inputs(ready: false)));
        Assert.Equal(GateDispatchStep.Wait, GateDispatchPlanner.Decide(Inputs(destination: "")));
        Assert.Equal(GateDispatchStep.Wait, GateDispatchPlanner.Decide(Inputs(destination: null)));
    }

    [Theory]
    [InlineData(FlightPhase.Cruise)]
    [InlineData(FlightPhase.Descent)]
    [InlineData(FlightPhase.Approach)]
    public void Airborne_DepartureStillLoaded_PicksTheAirport(FlightPhase phase)
        => Assert.Equal(GateDispatchStep.PickAirport, GateDispatchPlanner.Decide(Inputs(phase: phase)));

    [Fact]
    public void Airborne_NoAirportLoadedAtAll_PicksTheAirport()
        => Assert.Equal(GateDispatchStep.PickAirport, GateDispatchPlanner.Decide(Inputs(loaded: null)));

    [Theory]
    [InlineData(FlightPhase.Preflight)]
    [InlineData(FlightPhase.TaxiOut)]
    [InlineData(FlightPhase.Climb)]
    public void BeforeCruise_Waits(FlightPhase phase)
        => Assert.Equal(GateDispatchStep.Wait, GateDispatchPlanner.Decide(Inputs(phase: phase)));

    [Fact]
    public void PickBackoff_HoldsForTwoMinutes_ThenTriesAgain()
    {
        Assert.Equal(GateDispatchStep.Wait, GateDispatchPlanner.Decide(Inputs(lastPick: Now - TimeSpan.FromSeconds(30), attempts: 1)));
        Assert.Equal(GateDispatchStep.PickAirport, GateDispatchPlanner.Decide(Inputs(lastPick: Now - TimeSpan.FromMinutes(3), attempts: 1)));
    }

    [Fact]
    public void PickAttempts_Exhausted_Waits()
        => Assert.Equal(GateDispatchStep.Wait, GateDispatchPlanner.Decide(Inputs(
            lastPick: Now - TimeSpan.FromHours(1), attempts: GateDispatchPlanner.MaxAirportPickAttempts)));

    /// <summary>Issue #75 (2026-10-10): five page walks two minutes apart while GSX was still
    /// loading the airport. A real walk is followed by ten quiet minutes; three walks per flight.</summary>
    [Fact]
    public void WalkBackoff_HoldsForTenMinutes_ThenTriesAgain()
    {
        Assert.Equal(GateDispatchStep.Wait, GateDispatchPlanner.Decide(Inputs(
            lastPick: Now - TimeSpan.FromMinutes(5), attempts: 1, lastWalk: Now - TimeSpan.FromMinutes(5))));
        Assert.Equal(GateDispatchStep.PickAirport, GateDispatchPlanner.Decide(Inputs(
            lastPick: Now - TimeSpan.FromMinutes(11), attempts: 1, lastWalk: Now - TimeSpan.FromMinutes(11))));
        Assert.Equal(3, GateDispatchPlanner.MaxAirportPickAttempts);
    }

    [Fact]
    public void DestinationLoaded_InTheAir_Sends()
        => Assert.Equal(GateDispatchStep.Send, GateDispatchPlanner.Decide(Inputs(loaded: "EGLL", phase: FlightPhase.Descent)));

    [Fact]
    public void DestinationLoaded_CaseInsensitive_Sends()
        => Assert.Equal(GateDispatchStep.Send, GateDispatchPlanner.Decide(Inputs(loaded: "egll ", destination: "EGLL")));

    [Fact]
    public void DestinationLoadedByGsxAfterLanding_WhileRolling_Sends()
        => Assert.Equal(GateDispatchStep.Send, GateDispatchPlanner.Decide(Inputs(loaded: "EGLL", phase: FlightPhase.TaxiIn, data: Taxi())));

    [Fact]
    public void DestinationLoaded_ButParked_IsTooLate()
        => Assert.Equal(GateDispatchStep.TooLate, GateDispatchPlanner.Decide(Inputs(loaded: "EGLL", phase: FlightPhase.Shutdown, data: Parked())));

    [Fact]
    public void NoData_ParkedJudgedByPhase()
    {
        Assert.Equal(GateDispatchStep.TooLate, GateDispatchPlanner.Decide(Inputs(loaded: "EGLL", phase: FlightPhase.Shutdown, data: new FlightDataSnapshot { IsValid = false })));
        Assert.Equal(GateDispatchStep.Send, GateDispatchPlanner.Decide(Inputs(loaded: "EGLL", phase: FlightPhase.TaxiIn, data: new FlightDataSnapshot { IsValid = false })));
    }

    [Fact]
    public void NumberFallback_UniqueSuffixMatch_ReturnsItsNumber()
    {
        var parkings = new List<GsxParking>
        {
            new("Terminal 5B (531-548)|Stand 545R", "Stand 545R", "Stand 545R", 545, null, null),
            new("Terminal 5B (531-548)|Stand 545L", "Stand 545L", "Stand 545L", 546, null, null),
        };

        Assert.Equal(545, GsxGateResolver.NumberFallback(parkings, "545R"));
        Assert.Equal(545, GsxGateResolver.NumberFallback(parkings, "stand 545r"));
    }

    [Fact]
    public void NumberFallback_AmbiguousOrUnknownOrNoNumber_ReturnsNull()
    {
        var parkings = new List<GsxParking>
        {
            new("Gate D5", "D5", "Gate D5", 5, null, null),
            new("Stand D5", "D5", "Stand D5", 105, null, null),
            new("Gate D7", "D7", "Gate D7", null, null, null),
        };

        Assert.Null(GsxGateResolver.NumberFallback(parkings, "D5"));
        Assert.Null(GsxGateResolver.NumberFallback(parkings, "D9"));
        Assert.Null(GsxGateResolver.NumberFallback(parkings, "D7"));
        Assert.Null(GsxGateResolver.NumberFallback(parkings, ""));
    }

    // EHAM 2026-11-01 (issue #156): both in-flight picks reported Success and the gate.select
    // 10 ms later got no_airport; nothing re-sent until after touchdown.
    [Fact]
    public void AfterAPick_NoAirport_ResendsOnTheCadence_UpToTheCap()
    {
        var walked = Now.AddMinutes(-1);
        var refused = Now.AddSeconds(-70);
        Assert.Equal(GateDispatchStep.Send,
            GateDispatchPlanner.Decide(Inputs(lastWalk: walked, lastPick: walked, attempts: 1) with { LastNoAirportUtc = refused, SendsAfterPick = 1 }));
        // Too soon after the refusal: wait.
        Assert.Equal(GateDispatchStep.Wait,
            GateDispatchPlanner.Decide(Inputs(lastWalk: walked, lastPick: walked, attempts: 1) with { LastNoAirportUtc = Now.AddSeconds(-20), SendsAfterPick = 1 }));
        // The cap: after five sends the pick path (and its own backoff) decides again.
        Assert.Equal(GateDispatchStep.Wait,
            GateDispatchPlanner.Decide(Inputs(lastWalk: walked, lastPick: walked, attempts: 1) with { LastNoAirportUtc = refused, SendsAfterPick = GateDispatchPlanner.MaxSendsAfterPick }));
        // No pick ran: no re-send (the ground path handles it after landing).
        Assert.NotEqual(GateDispatchStep.Send,
            GateDispatchPlanner.Decide(Inputs(lastPick: walked, attempts: 0) with { LastNoAirportUtc = refused, SendsAfterPick = 1 }));
        // On the ground before departure: never.
        Assert.Equal(GateDispatchStep.Wait,
            GateDispatchPlanner.Decide(Inputs(phase: FlightPhase.TaxiOut, data: Taxi(), lastWalk: walked) with { LastNoAirportUtc = refused }));
    }
}
