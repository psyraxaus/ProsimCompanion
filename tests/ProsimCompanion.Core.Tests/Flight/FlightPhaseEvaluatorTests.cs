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
    public void Pushback_WithBeaconAndBrakeReleased_IsPushbackAndStart()
    {
        var snapshot = Ground() with { PushbackActive = true, BeaconOn = true, ParkBrakeSet = false };

        Assert.Equal(FlightPhase.PushbackAndStart, FlightPhaseEvaluator.Evaluate(snapshot, FlightPhase.Preflight));
    }

    [Fact]
    public void PushbackFlag_WithoutBeacon_StaysPreflight()
    {
        // Issue #100: groundservice.pushback is non-zero whenever the service is merely
        // connected (true at cold-and-dark on the 2026-08-22 flight) — the beacon is the
        // necessary gate, per Prosim2FO's hard-won rule.
        var snapshot = Ground() with { PushbackActive = true, ParkBrakeSet = false };

        Assert.Equal(FlightPhase.Preflight, FlightPhaseEvaluator.Evaluate(snapshot, FlightPhase.Preflight));
    }

    [Fact]
    public void BeaconAndApu_BrakeReleased_IsPushbackAndStart()
    {
        // Issue #100: during a GSX-driven push the pushback dataref reads 0 — beacon-on +
        // APU-running marks the real push window (the 2026-08-22 flight showed "Preflight"
        // for the whole actual pushback).
        var snapshot = Ground() with { BeaconOn = true, ApuRunning = true, ParkBrakeSet = false };

        Assert.Equal(FlightPhase.PushbackAndStart, FlightPhaseEvaluator.Evaluate(snapshot, FlightPhase.Preflight));
    }

    [Fact]
    public void PushbackFlag_WithBrakeSet_StaysPreflight()
    {
        // Smoke-test find: a noisy pushback flag on a parked aircraft must not fake a pushback.
        var snapshot = Ground() with { PushbackActive = true, BeaconOn = true, ApuRunning = true };

        Assert.Equal(FlightPhase.Preflight, FlightPhaseEvaluator.Evaluate(snapshot, FlightPhase.Preflight));
    }

    [Fact]
    public void TaxiOut_ConnectedPushbackReading_NeverWalksBack()
    {
        // Issue #100, 2026-08-22 10:57:58: stopped during taxi (gs=0, brake off) with the
        // "service connected" flag true — the phase regressed TaxiOut→PushbackAndStart and
        // spent the last 7 minutes of taxi there. Once the taxi has begun, only a genuine
        // engine start may regress.
        var snapshot = Ground() with
        {
            AnyEngineRunning = true,
            GroundSpeedKt = 0,
            ParkBrakeSet = false,
            PushbackActive = true,
            BeaconOn = true,
            ApuRunning = true,
        };

        Assert.Equal(FlightPhase.TaxiOut, FlightPhaseEvaluator.Evaluate(snapshot, FlightPhase.TaxiOut));
    }

    [Fact]
    public void EngineStart_MidTaxi_StillRegressesToPushbackAndStart()
    {
        // The cross-bleed-start path stays: a real engine start regresses from anywhere.
        var snapshot = Ground() with { AnyEngineRunning = true, EngineStarting = true, ParkBrakeSet = false };

        Assert.Equal(FlightPhase.PushbackAndStart, FlightPhaseEvaluator.Evaluate(snapshot, FlightPhase.TaxiOut));
    }

    [Fact]
    public void EngineStart_AtGateWithBrakeSet_IsPushbackAndStart()
    {
        var snapshot = Ground() with { EngineStarting = true };

        Assert.Equal(FlightPhase.PushbackAndStart, FlightPhaseEvaluator.Evaluate(snapshot, FlightPhase.Preflight));
    }

    [Fact]
    public void TaxiAfterPush_ApuStillRunning_ExitsToTaxiOut()
    {
        // Issue #104 (2026-08-23 flight): PushbackAndStart stuck through 14 minutes of taxi.
        // With the corrected pushback flag AND the walking-pace guard, taxiing with the
        // beacon on (always) and the APU still running must exit to TaxiOut.
        var snapshot = Ground() with
        {
            AnyEngineRunning = true,
            GroundSpeedKt = 15,
            ParkBrakeSet = false,
            BeaconOn = true,
            ApuRunning = true,
        };

        Assert.Equal(FlightPhase.TaxiOut, FlightPhaseEvaluator.Evaluate(snapshot, FlightPhase.PushbackAndStart));
    }

    [Fact]
    public void PushAtWalkingPace_BeaconAndApu_StaysPushbackAndStart()
    {
        // The real push window (beacon + APU, tug pace) still classifies as pushback.
        var snapshot = Ground() with
        {
            GroundSpeedKt = 4,
            ParkBrakeSet = false,
            BeaconOn = true,
            ApuRunning = true,
        };

        Assert.Equal(FlightPhase.PushbackAndStart, FlightPhaseEvaluator.Evaluate(snapshot, FlightPhase.PushbackAndStart));
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
    public void DepartureLevelOff_BelowFmsCruise_StaysClimb()
    {
        // Issue #105, 2026-08-23 verbatim: a SID level-off at 3,989 ft (VS +277) committed
        // Cruise on departure and the ISA advisory spoke at 4,000 ft.
        var snapshot = new FlightDataSnapshot
        {
            IsValid = true,
            OnGround = false,
            AircraftPowered = true,
            AnyEngineRunning = true,
            RadioAltitudeFt = 3978,
            AltitudeFt = 3989,
            VerticalSpeedFpm = 277,
            FmsCruiseAltFt = 36000,
        };

        Assert.Equal(FlightPhase.Climb, FlightPhaseEvaluator.Evaluate(snapshot, FlightPhase.Climb));
    }

    [Fact]
    public void ArrivalLevelOff_BelowFmsCruise_StaysDescent()
    {
        // Issue #105, 2026-08-23 verbatim: approach level-offs at 7,334/6,021 ft flipped
        // Descent→Cruise on the way into EGLL.
        var snapshot = new FlightDataSnapshot
        {
            IsValid = true,
            OnGround = false,
            AircraftPowered = true,
            AnyEngineRunning = true,
            RadioAltitudeFt = 7169,
            AltitudeFt = 7334,
            VerticalSpeedFpm = -6,
            FmsCruiseAltFt = 36000,
        };

        Assert.Equal(FlightPhase.Descent, FlightPhaseEvaluator.Evaluate(snapshot, FlightPhase.Descent));
    }

    [Fact]
    public void LevelNearFmsCruise_IsCruise()
    {
        var snapshot = new FlightDataSnapshot
        {
            IsValid = true,
            OnGround = false,
            AircraftPowered = true,
            AnyEngineRunning = true,
            RadioAltitudeFt = 30000,
            AltitudeFt = 35200,
            VerticalSpeedFpm = 40,
            FmsCruiseAltFt = 36000,
        };

        Assert.Equal(FlightPhase.Cruise, FlightPhaseEvaluator.Evaluate(snapshot, FlightPhase.Climb));
    }

    [Fact]
    public void LevelWithoutFmsCruise_BelowFloor_StaysClimb()
    {
        // No FMS cruise level entered: the conservative 10,000 ft floor gates cruise entry.
        var snapshot = new FlightDataSnapshot
        {
            IsValid = true,
            OnGround = false,
            AircraftPowered = true,
            AnyEngineRunning = true,
            RadioAltitudeFt = 7900,
            AltitudeFt = 8000,
            VerticalSpeedFpm = 0,
        };

        Assert.Equal(FlightPhase.Climb, FlightPhaseEvaluator.Evaluate(snapshot, FlightPhase.Climb));
    }

    [Fact]
    public void EstablishedCruise_StaysCruise_EvenBelowTheGate()
    {
        // The gate applies on ENTRY only — an established cruise (e.g. cruise level lowered
        // in the FMS mid-flight) never un-cruises on altitude alone.
        var snapshot = new FlightDataSnapshot
        {
            IsValid = true,
            OnGround = false,
            AircraftPowered = true,
            AnyEngineRunning = true,
            RadioAltitudeFt = 8000,
            AltitudeFt = 8000,
            VerticalSpeedFpm = 0,
            FmsCruiseAltFt = 36000,
        };

        Assert.Equal(FlightPhase.Cruise, FlightPhaseEvaluator.Evaluate(snapshot, FlightPhase.Cruise));
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
    public void ShallowDescentFromCruise_StaysCruise()
    {
        // Hysteresis (issue #59): four Descent<->Cruise flip-flops in 23 minutes on the
        // 2026-08-16 flight — entering Descent now needs a decisive rate (> 500 fpm down).
        var snapshot = new FlightDataSnapshot
        {
            IsValid = true,
            OnGround = false,
            AircraftPowered = true,
            AnyEngineRunning = true,
            RadioAltitudeFt = 30000,
            AltitudeFt = 36000,
            VerticalSpeedFpm = -400,
        };

        Assert.Equal(FlightPhase.Cruise, FlightPhaseEvaluator.Evaluate(snapshot, FlightPhase.Cruise));
    }

    [Fact]
    public void EstablishedDescent_HoldsThroughShallowSegment()
    {
        // Once established in Descent, a shallow segment (level-off capture, step descent)
        // down to -100 fpm still counts as descending.
        var snapshot = new FlightDataSnapshot
        {
            IsValid = true,
            OnGround = false,
            AircraftPowered = true,
            AnyEngineRunning = true,
            RadioAltitudeFt = 20000,
            AltitudeFt = 24000,
            VerticalSpeedFpm = -150,
        };

        Assert.Equal(FlightPhase.Descent, FlightPhaseEvaluator.Evaluate(snapshot, FlightPhase.Descent));
    }

    [Fact]
    public void EstablishedDescent_ReturnsToCruiseOnlyNearLevel()
    {
        var snapshot = new FlightDataSnapshot
        {
            IsValid = true,
            OnGround = false,
            AircraftPowered = true,
            AnyEngineRunning = true,
            RadioAltitudeFt = 20000,
            AltitudeFt = 24000,
            VerticalSpeedFpm = -50,
        };

        Assert.Equal(FlightPhase.Cruise, FlightPhaseEvaluator.Evaluate(snapshot, FlightPhase.Descent));
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
    public void ClimbingWithGearDown_StaysClimb()
    {
        // Issue #48: a gear lever stuck down (dead hardware panel) flipped Climb->Approach at
        // +2000 fpm and the cabin announced "secure for landing" on climb-out.
        var snapshot = new FlightDataSnapshot
        {
            IsValid = true,
            OnGround = false,
            AircraftPowered = true,
            AnyEngineRunning = true,
            RadioAltitudeFt = 1800,
            VerticalSpeedFpm = 2000,
            GearDown = true,
        };

        Assert.Equal(FlightPhase.Climb, FlightPhaseEvaluator.Evaluate(snapshot, FlightPhase.Climb));
    }

    [Fact]
    public void LevelWithGearDown_LowOverGround_IsStillApproach()
    {
        // The gear-down arm keeps working for a level approach segment — only a genuine climb
        // outvotes it.
        var snapshot = new FlightDataSnapshot
        {
            IsValid = true,
            OnGround = false,
            AircraftPowered = true,
            AnyEngineRunning = true,
            RadioAltitudeFt = 1500,
            VerticalSpeedFpm = 0,
            GearDown = true,
        };

        Assert.Equal(FlightPhase.Approach, FlightPhaseEvaluator.Evaluate(snapshot, FlightPhase.Cruise));
    }

    [Fact]
    public void Approach_LevelOffClimbBlip_StaysApproach()
    {
        // Issue #99, 2026-08-22 LGAV 14:09:06 verbatim: +388 fpm during a level-off at
        // 3,000 ft (gear still up) flipped Approach→Climb and the FO called "positive climb"
        // on final.
        var snapshot = new FlightDataSnapshot
        {
            IsValid = true,
            OnGround = false,
            AircraftPowered = true,
            AnyEngineRunning = true,
            IndicatedAirspeedKt = 189.9,
            RadioAltitudeFt = 3003,
            VerticalSpeedFpm = 388,
        };

        Assert.Equal(FlightPhase.Approach, FlightPhaseEvaluator.Evaluate(snapshot, FlightPhase.Approach));
    }

    [Fact]
    public void Approach_GoAroundGradeClimb_ExitsToInitialClimb()
    {
        var snapshot = new FlightDataSnapshot
        {
            IsValid = true,
            OnGround = false,
            AircraftPowered = true,
            AnyEngineRunning = true,
            IndicatedAirspeedKt = 160,
            RadioAltitudeFt = 2500,
            VerticalSpeedFpm = 2200,
            GearDown = true,
        };

        // InitialClimb, not Climb (review 2026-08-29): the missed-approach re-brief, the
        // callouts engine and the stabilised-approach monitor all detect a go-around on the
        // Approach/LandingRollout → InitialClimb edge.
        Assert.Equal(FlightPhase.InitialClimb, FlightPhaseEvaluator.Evaluate(snapshot, FlightPhase.Approach));
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
