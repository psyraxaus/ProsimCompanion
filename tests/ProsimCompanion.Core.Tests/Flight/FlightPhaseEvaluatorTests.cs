using ProsimCompanion.Core.Flight;
using Xunit;

namespace ProsimCompanion.Core.Tests.Flight;

public sealed class FlightPhaseEvaluatorTests
{
    private static FlightDataSnapshot Ground(bool powered = true) => new()
    {
        IsValid = true,
        OnGround = true,
        AircraftPowered = powered,
        ParkBrakeSet = true,
        GearDown = true,
    };

    [Fact]
    public void InvalidSnapshot_HoldsCurrentPhase()
    {
        var snapshot = new FlightDataSnapshot { IsValid = false };

        Assert.Equal(FlightPhase.Cruise, FlightPhaseEvaluator.Evaluate(snapshot, FlightPhase.Cruise));
    }

    [Fact]
    public void Unpowered_OnGround_IsColdAndDark()
        => Assert.Equal(FlightPhase.ColdAndDark, FlightPhaseEvaluator.Evaluate(Ground(powered: false), FlightPhase.Unknown));

    [Fact]
    public void Powered_NoEngines_IsPreflight()
        => Assert.Equal(FlightPhase.Preflight, FlightPhaseEvaluator.Evaluate(Ground(), FlightPhase.ColdAndDark));

    [Fact]
    public void Pushback_WithBrakeReleased_IsPushbackAndStart()
    {
        var snapshot = Ground() with { PushbackActive = true, ParkBrakeSet = false };

        Assert.Equal(FlightPhase.PushbackAndStart, FlightPhaseEvaluator.Evaluate(snapshot, FlightPhase.Preflight));
    }

    [Fact]
    public void PushbackFlag_WithBrakeSet_StaysPreflight()
    {
        // Smoke-test find: a noisy pushback flag on a parked aircraft must not fake a pushback.
        var snapshot = Ground() with { PushbackActive = true };

        Assert.Equal(FlightPhase.Preflight, FlightPhaseEvaluator.Evaluate(snapshot, FlightPhase.Preflight));
    }

    [Fact]
    public void EngineStart_AtGateWithBrakeSet_IsPushbackAndStart()
    {
        var snapshot = Ground() with { EngineStarting = true };

        Assert.Equal(FlightPhase.PushbackAndStart, FlightPhaseEvaluator.Evaluate(snapshot, FlightPhase.Preflight));
    }

    [Theory]
    [InlineData(FlightPhase.Cruise)]
    [InlineData(FlightPhase.InitialClimb)]
    [InlineData(FlightPhase.Climb)]
    public void SpawnRecovery_FlightPhaseOnGroundEnginesOffStationary_IsPreflight(FlightPhase current)
    {
        // Smoke-test find: ProSim reports airborne defaults while MSFS loads; once the aircraft
        // materializes at the gate, the engine must recover to Preflight — never fall into
        // pushback/rollout logic from a stale flight phase.
        var snapshot = Ground() with { PushbackActive = true, ParkBrakeSet = false };

        Assert.Equal(FlightPhase.Preflight, FlightPhaseEvaluator.Evaluate(snapshot, current));
    }

    [Fact]
    public void EnginesRunning_Moving_IsTaxiOut()
    {
        var snapshot = Ground() with { AnyEngineRunning = true, GroundSpeedKt = 12, ParkBrakeSet = false };

        Assert.Equal(FlightPhase.TaxiOut, FlightPhaseEvaluator.Evaluate(snapshot, FlightPhase.PushbackAndStart));
    }

    [Fact]
    public void EnginesRunning_ParkedAfterStart_StaysPushbackAndStart()
    {
        var snapshot = Ground() with { AnyEngineRunning = true, GroundSpeedKt = 0 };

        Assert.Equal(FlightPhase.PushbackAndStart, FlightPhaseEvaluator.Evaluate(snapshot, FlightPhase.PushbackAndStart));
    }

    [Fact]
    public void TakeoffThrust_OnGround_IsTakeoffRoll()
    {
        var snapshot = Ground() with { AnyEngineRunning = true, TakeoffThrustSet = true, ParkBrakeSet = false };

        Assert.Equal(FlightPhase.TakeoffRoll, FlightPhaseEvaluator.Evaluate(snapshot, FlightPhase.TaxiOut));
    }

    [Fact]
    public void Airborne_FromTakeoffRoll_IsInitialClimb()
    {
        var snapshot = new FlightDataSnapshot
        {
            IsValid = true,
            OnGround = false,
            AircraftPowered = true,
            AnyEngineRunning = true,
            RadioAltitudeFt = 300,
            VerticalSpeedFpm = 2000,
            IndicatedAirspeedKt = 165,
        };

        Assert.Equal(FlightPhase.InitialClimb, FlightPhaseEvaluator.Evaluate(snapshot, FlightPhase.TakeoffRoll));
    }

    [Fact]
    public void HighClimb_IsClimb()
    {
        var snapshot = new FlightDataSnapshot
        {
            IsValid = true,
            OnGround = false,
            AircraftPowered = true,
            AnyEngineRunning = true,
            RadioAltitudeFt = 4000,
            AltitudeFt = 8000,
            VerticalSpeedFpm = 1800,
        };

        Assert.Equal(FlightPhase.Climb, FlightPhaseEvaluator.Evaluate(snapshot, FlightPhase.InitialClimb));
    }

    [Fact]
    public void LevelFlight_IsCruise()
    {
        var snapshot = new FlightDataSnapshot
        {
            IsValid = true,
            OnGround = false,
            AircraftPowered = true,
            AnyEngineRunning = true,
            RadioAltitudeFt = 30000,
            AltitudeFt = 36000,
            VerticalSpeedFpm = 50,
        };

        Assert.Equal(FlightPhase.Cruise, FlightPhaseEvaluator.Evaluate(snapshot, FlightPhase.Climb));
    }

    [Fact]
    public void Descending_IsDescent()
    {
        var snapshot = new FlightDataSnapshot
        {
            IsValid = true,
            OnGround = false,
            AircraftPowered = true,
            AnyEngineRunning = true,
            RadioAltitudeFt = 20000,
            AltitudeFt = 24000,
            VerticalSpeedFpm = -1800,
        };

        Assert.Equal(FlightPhase.Descent, FlightPhaseEvaluator.Evaluate(snapshot, FlightPhase.Cruise));
    }

    [Fact]
    public void LowWithGearDown_IsApproach()
    {
        var snapshot = new FlightDataSnapshot
        {
            IsValid = true,
            OnGround = false,
            AircraftPowered = true,
            AnyEngineRunning = true,
            RadioAltitudeFt = 1800,
            VerticalSpeedFpm = -700,
            GearDown = true,
        };

        Assert.Equal(FlightPhase.Approach, FlightPhaseEvaluator.Evaluate(snapshot, FlightPhase.Descent));
    }

    [Fact]
    public void Touchdown_FromApproach_IsLandingRollout()
    {
        var snapshot = Ground() with { AnyEngineRunning = true, IndicatedAirspeedKt = 120, ParkBrakeSet = false };

        Assert.Equal(FlightPhase.LandingRollout, FlightPhaseEvaluator.Evaluate(snapshot, FlightPhase.Approach));
    }

    [Fact]
    public void RolloutDecelerated_IsTaxiIn()
    {
        var snapshot = Ground() with { AnyEngineRunning = true, IndicatedAirspeedKt = 20, ParkBrakeSet = false };

        Assert.Equal(FlightPhase.TaxiIn, FlightPhaseEvaluator.Evaluate(snapshot, FlightPhase.LandingRollout));
    }

    [Fact]
    public void EnginesCut_AfterTaxiIn_IsShutdown()
    {
        var snapshot = Ground() with { AnyEngineRunning = false };

        Assert.Equal(FlightPhase.Shutdown, FlightPhaseEvaluator.Evaluate(snapshot, FlightPhase.TaxiIn));
    }

    [Fact]
    public void FullFlight_TransitionsInOrder()
    {
        // A whole flight walked through the evaluator, phase by phase.
        var phase = FlightPhase.Unknown;
        void Step(FlightDataSnapshot s, FlightPhase expected)
        {
            phase = FlightPhaseEvaluator.Evaluate(s, phase);
            Assert.Equal(expected, phase);
        }

        Step(Ground(powered: false), FlightPhase.ColdAndDark);
        Step(Ground(), FlightPhase.Preflight);
        Step(Ground() with { PushbackActive = true, EngineStarting = true, ParkBrakeSet = false }, FlightPhase.PushbackAndStart);
        Step(Ground() with { AnyEngineRunning = true, GroundSpeedKt = 15, ParkBrakeSet = false }, FlightPhase.TaxiOut);
        Step(Ground() with { AnyEngineRunning = true, TakeoffThrustSet = true, ParkBrakeSet = false, IndicatedAirspeedKt = 80 }, FlightPhase.TakeoffRoll);
        Step(new FlightDataSnapshot { IsValid = true, OnGround = false, AircraftPowered = true, AnyEngineRunning = true, RadioAltitudeFt = 500, VerticalSpeedFpm = 2500 }, FlightPhase.InitialClimb);
        Step(new FlightDataSnapshot { IsValid = true, OnGround = false, AircraftPowered = true, AnyEngineRunning = true, RadioAltitudeFt = 5000, AltitudeFt = 10000, VerticalSpeedFpm = 2000 }, FlightPhase.Climb);
        Step(new FlightDataSnapshot { IsValid = true, OnGround = false, AircraftPowered = true, AnyEngineRunning = true, RadioAltitudeFt = 30000, AltitudeFt = 36000, VerticalSpeedFpm = 0 }, FlightPhase.Cruise);
        Step(new FlightDataSnapshot { IsValid = true, OnGround = false, AircraftPowered = true, AnyEngineRunning = true, RadioAltitudeFt = 15000, AltitudeFt = 18000, VerticalSpeedFpm = -2000 }, FlightPhase.Descent);
        Step(new FlightDataSnapshot { IsValid = true, OnGround = false, AircraftPowered = true, AnyEngineRunning = true, RadioAltitudeFt = 1500, VerticalSpeedFpm = -700, GearDown = true }, FlightPhase.Approach);
        Step(Ground() with { AnyEngineRunning = true, IndicatedAirspeedKt = 130, ParkBrakeSet = false }, FlightPhase.LandingRollout);
        Step(Ground() with { AnyEngineRunning = true, IndicatedAirspeedKt = 15, ParkBrakeSet = false }, FlightPhase.TaxiIn);
        Step(Ground() with { AnyEngineRunning = false }, FlightPhase.Shutdown);
    }
}
