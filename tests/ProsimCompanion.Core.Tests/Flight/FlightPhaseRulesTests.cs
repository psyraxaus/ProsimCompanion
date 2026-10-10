using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Flight;
using Xunit;

namespace ProsimCompanion.Core.Tests.Flight;

/// <summary>Rules added by the 2026-08-29 robustness review, plus the decision contract
/// (rule id, reason, debounce) the engine and the replay harness depend on.</summary>
public sealed class FlightPhaseRulesTests
{
    private static FlightDataSnapshot Parked() => new()
    {
        IsValid = true,
        OnGround = true,
        AircraftPowered = true,
        ParkBrakeSet = true,
        GearDown = true,
    };

    private static PhaseDecision? Decide(FlightDataSnapshot s, FlightPhase current, FlightStateOptions? o = null)
        => FlightPhaseEvaluator.Decide(s, current, o ?? FlightStateOptions.Default);

    // ---- Turnaround (the gap: Shutdown could only leave via ColdAndDark) ----

    [Fact]
    public void Shutdown_ArrivalCompleteAndParked_TurnsAroundToPreflight_AfterTheHold()
    {
        var decision = Decide(Parked() with { ArrivalComplete = true }, FlightPhase.Shutdown);

        Assert.NotNull(decision);
        Assert.Equal(FlightPhase.Preflight, decision.Target);
        Assert.Equal("turnaround-arrival-complete", decision.RuleId);
        Assert.Equal(TimeSpan.FromSeconds(FlightStateOptions.Default.TurnaroundHoldSeconds), decision.Debounce);
    }

    [Fact]
    public void Shutdown_ParkedButStillDeboarding_Holds()
        // 2026-08-29 ESSA: a time-only turnaround flipped to Preflight 30 s after shutdown
        // and ground prep repositioned the aircraft under the passengers.
        => Assert.Null(Decide(Parked(), FlightPhase.Shutdown));

    [Fact]
    public void Shutdown_BeaconStillOn_Holds()
        => Assert.Null(Decide(Parked() with { BeaconOn = true }, FlightPhase.Shutdown));

    [Fact]
    public void Shutdown_BeaconOnEngineStart_IsPushbackAndStart()
    {
        var decision = Decide(Parked() with { BeaconOn = true, EngineStarting = true }, FlightPhase.Shutdown);

        Assert.Equal(FlightPhase.PushbackAndStart, decision?.Target);
        Assert.Equal("turnaround-start", decision?.RuleId);
    }

    [Fact]
    public void Shutdown_BeaconOnEnginesRunning_IsPushbackAndStart_NotTaxiIn()
    {
        // The old tree read a post-shutdown engine start as TaxiIn (arrival context won).
        var decision = Decide(Parked() with { BeaconOn = true, AnyEngineRunning = true }, FlightPhase.Shutdown);

        Assert.Equal(FlightPhase.PushbackAndStart, decision?.Target);
    }

    [Fact]
    public void Shutdown_EnginesRunningWithoutBeacon_Holds()
        // A state-string flicker after shutdown is not a turnaround: the beacon corroborates.
        => Assert.Null(Decide(Parked() with { AnyEngineRunning = true }, FlightPhase.Shutdown));

    [Fact]
    public void TaxiIn_EnginesCutParked_GoesToShutdownFirst_NotStraightToPreflight()
        => Assert.Equal(FlightPhase.Shutdown, FlightPhaseEvaluator.Evaluate(Parked(), FlightPhase.TaxiIn));

    [Fact]
    public void TaxiIn_EnginesOffWhileRolling_StaysTaxiIn()
    {
        // Engines reading off at 20 kt with the brake released is a blip or a tow, not the stand.
        var rolling = Parked() with { ParkBrakeSet = false, GroundSpeedKt = 20 };

        Assert.Equal(FlightPhase.TaxiIn, FlightPhaseEvaluator.Evaluate(rolling, FlightPhase.TaxiIn));
    }

    // ---- Take-off / landing edge hardening ----

    [Fact]
    public void TakeoffThrust_WithParkBrakeSet_IsNotATakeoffRoll()
    {
        var runUp = Parked() with { AnyEngineRunning = true, TakeoffThrustSet = true, GroundSpeedKt = 0 };

        Assert.Equal(FlightPhase.PushbackAndStart, FlightPhaseEvaluator.Evaluate(runUp, FlightPhase.PushbackAndStart));
    }

    [Fact]
    public void LandingRollout_BounceBelow50ft_Holds()
    {
        var bounce = new FlightDataSnapshot
        {
            IsValid = true,
            OnGround = false,
            AircraftPowered = true,
            AnyEngineRunning = true,
            IndicatedAirspeedKt = 125,
            RadioAltitudeFt = 15,
            VerticalSpeedFpm = 200,
        };

        Assert.Null(Decide(bounce, FlightPhase.LandingRollout));
    }

    [Fact]
    public void LandingRollout_GoAroundClimb_IsInitialClimb()
    {
        var goAround = new FlightDataSnapshot
        {
            IsValid = true,
            OnGround = false,
            AircraftPowered = true,
            AnyEngineRunning = true,
            IndicatedAirspeedKt = 150,
            RadioAltitudeFt = 80,
            VerticalSpeedFpm = 1800,
        };

        var decision = Decide(goAround, FlightPhase.LandingRollout);

        Assert.Equal(FlightPhase.InitialClimb, decision?.Target);
        Assert.Equal("go-around", decision?.RuleId);
    }

    [Fact]
    public void Climb_GroundContactAtSpeed_IsLandingRollout_NotTaxiOut()
    {
        // A landing that never met the Approach criteria (visual circuit) still rolls out.
        var contact = Parked() with { AnyEngineRunning = true, ParkBrakeSet = false, IndicatedAirspeedKt = 120 };

        var decision = Decide(contact, FlightPhase.Climb);

        Assert.Equal(FlightPhase.LandingRollout, decision?.Target);
        Assert.Equal("unexpected-touchdown", decision?.RuleId);
    }

    [Fact]
    public void Climb_GroundContactSlowEnginesRunning_IsTaxiIn()
    {
        var slow = Parked() with { AnyEngineRunning = true, ParkBrakeSet = false, IndicatedAirspeedKt = 10, GroundSpeedKt = 12 };

        Assert.Equal(FlightPhase.TaxiIn, FlightPhaseEvaluator.Evaluate(slow, FlightPhase.Climb));
    }

    // ---- Decision contract ----

    [Fact]
    public void Decide_ReportsRuleIdReasonAndDebounce()
    {
        var roll = Parked() with { AnyEngineRunning = true, TakeoffThrustSet = true, ParkBrakeSet = false, MaxN1Percent = 88 };

        var decision = Decide(roll, FlightPhase.TaxiOut);

        Assert.NotNull(decision);
        Assert.Equal("takeoff-thrust", decision.RuleId);
        Assert.Equal(TimeSpan.Zero, decision.Debounce);
        Assert.Contains("take-off thrust", decision.Reason);
        Assert.Contains("88", decision.Reason);
    }

    // ---- Issue #161: a push in progress holds PushbackAndStart (EFHK Gate 46, 2026-10-10) ----

    [Fact]
    public void PushbackAndStart_BrakeReSetDuringThePush_Holds()
    {
        // 04:12:42Z: brake set for the tug, gs 0.5, beacon on, pushback flag true, engines off.
        var pushing = Parked() with { BeaconOn = true, ApuRunning = true, PushbackActive = true, GroundSpeedKt = 0.5 };

        Assert.Null(Decide(pushing, FlightPhase.PushbackAndStart));
    }

    [Fact]
    public void PushbackAndStart_SpuriousGroundSpeedDuringThePush_Holds()
    {
        // 04:15:44Z: GSX froze the position, ground speed read 16.8 kt above PushbackMaxGroundSpeedKt.
        var pushing = Parked() with { BeaconOn = true, ApuRunning = true, PushbackActive = true, ParkBrakeSet = false, GroundSpeedKt = 16.8 };

        Assert.Null(Decide(pushing, FlightPhase.PushbackAndStart));
    }

    [Fact]
    public void PushbackAndStart_PushFlagDropsWithEnginesOff_StillRegressesAfterTheLongDebounce()
    {
        // Without the flag the catch-all keeps its job (issue #59 semantics unchanged).
        var decision = Decide(Parked(), FlightPhase.PushbackAndStart);

        Assert.Equal(FlightPhase.Preflight, decision?.Target);
        Assert.Equal("preflight", decision?.RuleId);
    }

    [Fact]
    public void PushbackAndStart_EnginesRunningAndRolling_StillLeavesToTaxiOut()
    {
        // The hold needs engines OFF: with them running and the aircraft past tug speed the
        // taxi-out rule decides exactly as before (push-evidence still wins below tug speed, #138).
        var rolling = Parked() with { BeaconOn = true, PushbackActive = true, ParkBrakeSet = false, AnyEngineRunning = true, GroundSpeedKt = 12 };

        var decision = Decide(rolling, FlightPhase.PushbackAndStart);

        Assert.Equal(FlightPhase.TaxiOut, decision?.Target);
        Assert.Equal("taxi-out", decision?.RuleId);
    }

    // ---- Issue #163: a level-off wobble in the descent is not a climb (EGCC 2026-10-10) ----

    [Fact]
    public void Descent_ClimbRate_CarriesTheDescentToClimbSettle()
    {
        var levelOff = new FlightDataSnapshot { IsValid = true, AircraftPowered = true, AnyEngineRunning = true, AltitudeFt = 5916, RadioAltitudeFt = 5360, VerticalSpeedFpm = 357 };

        var decision = Decide(levelOff, FlightPhase.Descent);

        Assert.Equal(FlightPhase.Climb, decision?.Target);
        Assert.Equal("climb", decision?.RuleId);
        Assert.Equal(TimeSpan.FromSeconds(FlightStateOptions.Default.DescentToClimbSettleSeconds), decision?.Debounce);
        Assert.True(FlightStateOptions.Default.DescentToClimbSettleSeconds >= 5, "the settle must outlive a three-second wobble");
    }

    [Fact]
    public void DepartureRegression_CarriesTheLongDebounce()
    {
        var decision = Decide(Parked(), FlightPhase.TaxiOut);

        Assert.Equal(FlightPhase.Preflight, decision?.Target);
        Assert.Equal(TimeSpan.FromSeconds(FlightStateOptions.Default.DepartureRegressionSeconds), decision?.Debounce);
    }

    [Fact]
    public void Options_DriveTheThresholds()
    {
        var descending = new FlightDataSnapshot
        {
            IsValid = true,
            OnGround = false,
            AircraftPowered = true,
            AnyEngineRunning = true,
            IndicatedAirspeedKt = 200,
            RadioAltitudeFt = 2000,
            VerticalSpeedFpm = -900,
        };

        Assert.Equal(FlightPhase.Approach, Decide(descending, FlightPhase.Descent)?.Target);

        var lowerGate = new FlightStateOptions { ApproachRaCeilingFt = 1000 };
        Assert.Null(Decide(descending, FlightPhase.Descent, lowerGate));
    }

    [Fact]
    public void EveryRule_HasAUniqueId()
    {
        var ids = FlightPhaseRules.All.Select(r => r.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ids, id => Assert.Matches("^[a-z0-9-]+$", id));
    }

    [Fact]
    public void InvalidSnapshot_DecidesNothing()
        => Assert.Null(Decide(new FlightDataSnapshot { IsValid = false }, FlightPhase.Cruise));

    // ---- Departure (owner decision 2026-09-20: boarding underway → Departure, held to the push) ----

    [Fact]
    public void Preflight_BoardingStarted_IsDeparture()
    {
        var decision = Decide(Parked() with { BoardingStarted = true }, FlightPhase.Preflight);

        Assert.Equal(FlightPhase.Departure, decision?.Target);
        Assert.Equal("boarding", decision?.RuleId);
        Assert.Equal(TimeSpan.FromSeconds(FlightStateOptions.Default.DefaultDebounceSeconds), decision?.Debounce);
    }

    [Fact]
    public void Preflight_WithoutBoarding_NeverReadsDeparture()
        => Assert.NotEqual(FlightPhase.Departure, Decide(Parked(), FlightPhase.Preflight)?.Target);

    [Fact]
    public void Departure_HoldsWhenBoardingCompletesOrTheBeaconFlickers()
    {
        // The latch is the engine's; at rule level Departure is sticky on any parked,
        // engines-off evidence — boarding done, doors closed, beacon on with the brake set.
        Assert.Null(Decide(Parked(), FlightPhase.Departure));
        Assert.Null(Decide(Parked() with { BeaconOn = true }, FlightPhase.Departure));
        Assert.Null(Decide(Parked() with { BeaconOn = true, ApuRunning = true }, FlightPhase.Departure));
    }

    [Fact]
    public void Departure_PushEvidence_IsPushbackAndStart()
    {
        var decision = Decide(
            Parked() with { BeaconOn = true, ApuRunning = true, ParkBrakeSet = false }, FlightPhase.Departure);

        Assert.Equal(FlightPhase.PushbackAndStart, decision?.Target);
        Assert.Equal("push-evidence", decision?.RuleId);
    }

    [Fact]
    public void Departure_EngineStart_IsPushbackAndStart()
        => Assert.Equal(
            FlightPhase.PushbackAndStart,
            Decide(Parked() with { EngineStarting = true }, FlightPhase.Departure)?.Target);

    [Fact]
    public void Departure_EnginesRunning_IsTaxiOut()
        => Assert.Equal(
            FlightPhase.TaxiOut,
            Decide(Parked() with { AnyEngineRunning = true, ParkBrakeSet = false }, FlightPhase.Departure)?.Target);

    [Fact]
    public void Departure_PowerOff_IsColdAndDark()
        => Assert.Equal(
            FlightPhase.ColdAndDark,
            Decide(Parked() with { AircraftPowered = false }, FlightPhase.Departure)?.Target);

    [Fact]
    public void Departure_Liftoff_IsInitialClimb()
    {
        // Spawn-in-the-air / data glitch parity with Preflight: Departure is a liftoff source.
        var airborne = new FlightDataSnapshot
        {
            IsValid = true, OnGround = false, AircraftPowered = true, AnyEngineRunning = true,
            IndicatedAirspeedKt = 150, RadioAltitudeFt = 100, VerticalSpeedFpm = 1500,
        };

        Assert.Equal(FlightPhase.InitialClimb, Decide(airborne, FlightPhase.Departure)?.Target);
    }
}
