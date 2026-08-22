using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using Xunit;

namespace ProsimCompanion.Core.Tests.Flight;

public sealed class FlightStateEngineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 16, 7, 39, 0, TimeSpan.Zero);
    private static readonly TimeSpan Tick = FlightStateEngine.TickInterval;

    private sealed class NullFlightSource : IFlightDataSource
    {
        public FlightDataSnapshot Sample() => new() { IsValid = false };
    }

    private static FlightStateEngine Create(out SimSessionStore session)
    {
        session = new SimSessionStore();
        return new FlightStateEngine(new NullFlightSource(), session, NullLogger<FlightStateEngine>.Instance);
    }

    private static void SetSession(SimSessionStore store, SimSessionPhase phase)
        => store.Publish(new SimSessionSnapshot(phase, SimRunning: true, Paused: false, CameraState: null, SimVersion: null));

    /// <summary>A fully-registered, powered, parked-at-the-gate snapshot (target: Preflight).</summary>
    private static FlightDataSnapshot ReadyGround() => new()
    {
        IsValid = true,
        IsReady = true,
        OnGround = true,
        AircraftPowered = true,
        ParkBrakeSet = true,
        GearDown = true,
    };

    /// <summary>The 2026-08-16 startup snapshot shape: valid-looking but half-registered —
    /// defaults read as airborne with gear down at RA 0 and would evaluate to Approach.</summary>
    private static FlightDataSnapshot HalfRegisteredStartup() => new()
    {
        IsValid = true,
        IsReady = false,
        OnGround = false,
        GearDown = true,
        RadioAltitudeFt = 0,
        IndicatedAirspeedKt = 0,
        GroundSpeedKt = 0,
        VerticalSpeedFpm = 0,
        AircraftPowered = true,
    };

    /// <summary>Drives identical snapshots until just past the given hold time.</summary>
    private static DateTimeOffset Drive(FlightStateEngine engine, FlightDataSnapshot snapshot, DateTimeOffset from, TimeSpan hold)
    {
        var now = from;
        var end = from + hold;
        while (now <= end)
        {
            engine.ProcessTick(snapshot, now);
            now += Tick;
        }

        return now;
    }

    [Fact]
    public void StaysUnknown_WhileSessionNotActive()
    {
        var engine = Create(out var session);

        // Session Unknown (SimConnect not concluded anything yet) — even a perfectly ready
        // snapshot must not classify.
        Drive(engine, ReadyGround(), T0, TimeSpan.FromSeconds(10));
        Assert.Equal(FlightPhase.Unknown, engine.CurrentPhase);

        SetSession(session, SimSessionPhase.NotInSession);
        Drive(engine, ReadyGround(), T0 + TimeSpan.FromSeconds(11), TimeSpan.FromSeconds(10));
        Assert.Equal(FlightPhase.Unknown, engine.CurrentPhase);
    }

    [Fact]
    public void StaysUnknown_WhileDatarefsStillRegistering()
    {
        // The 2026-08-16 bug shape: session live, but the half-registered snapshot would have
        // evaluated Unknown->Approach at ias=0/gs=0. The readiness flag must block it.
        var engine = Create(out var session);
        SetSession(session, SimSessionPhase.InSession);

        Drive(engine, HalfRegisteredStartup(), T0, TimeSpan.FromSeconds(10));

        Assert.Equal(FlightPhase.Unknown, engine.CurrentPhase);
    }

    [Fact]
    public void Classifies_OnceSessionAndDataAreReady()
    {
        var engine = Create(out var session);
        SetSession(session, SimSessionPhase.InSession);

        Drive(engine, ReadyGround(), T0, TimeSpan.FromSeconds(2));

        Assert.Equal(FlightPhase.Preflight, engine.CurrentPhase);
    }

    [Fact]
    public void Walkaround_CountsAsInSession_ForClassification()
    {
        // SimSessionSnapshot.InSession covers walkaround — the aircraft data is real while
        // the pilot walks around it; ground automation gates elsewhere.
        var engine = Create(out var session);
        SetSession(session, SimSessionPhase.Walkaround);

        Drive(engine, ReadyGround(), T0, TimeSpan.FromSeconds(2));

        Assert.Equal(FlightPhase.Preflight, engine.CurrentPhase);
    }

    [Fact]
    public void DepartureRegression_RequiresSustainedEvidence()
    {
        var engine = Create(out var session);
        SetSession(session, SimSessionPhase.InSession);

        var now = Drive(engine, ReadyGround(), T0, TimeSpan.FromSeconds(2));
        Assert.Equal(FlightPhase.Preflight, engine.CurrentPhase);

        now = Drive(engine, ReadyGround() with { EngineStarting = true }, now, TimeSpan.FromSeconds(2));
        Assert.Equal(FlightPhase.PushbackAndStart, engine.CurrentPhase);

        // Engines-off/no-start snapshots target Preflight — a regression. The normal 1 s
        // debounce must NOT commit it...
        now = Drive(engine, ReadyGround(), now, TimeSpan.FromSeconds(2));
        Assert.Equal(FlightPhase.PushbackAndStart, engine.CurrentPhase);

        // ...but sustained contradictory evidence (5 s pair debounce) still corrects, so a
        // genuinely bogus PushbackAndStart never sticks forever.
        Drive(engine, ReadyGround(), now, TimeSpan.FromSeconds(4));
        Assert.Equal(FlightPhase.Preflight, engine.CurrentPhase);
    }

    [Fact]
    public void DepartureRegression_BlipDoesNotResetPhase()
    {
        var engine = Create(out var session);
        SetSession(session, SimSessionPhase.InSession);

        var now = Drive(engine, ReadyGround() with { EngineStarting = true }, T0, TimeSpan.FromSeconds(2));
        Assert.Equal(FlightPhase.PushbackAndStart, engine.CurrentPhase);

        // A 2 s both-engines-off glitch (state-string misread) then normality restored.
        now = Drive(engine, ReadyGround(), now, TimeSpan.FromSeconds(2));
        Drive(engine, ReadyGround() with { EngineStarting = true }, now, TimeSpan.FromSeconds(2));

        Assert.Equal(FlightPhase.PushbackAndStart, engine.CurrentPhase);
    }

    [Fact]
    public void DescentToCruise_NeedsLongSettle()
    {
        var engine = Create(out var session);
        SetSession(session, SimSessionPhase.InSession);

        var airborneDescent = new FlightDataSnapshot
        {
            IsValid = true,
            IsReady = true,
            OnGround = false,
            AircraftPowered = true,
            AnyEngineRunning = true,
            IndicatedAirspeedKt = 280,
            GroundSpeedKt = 420,
            RadioAltitudeFt = 20000,
            AltitudeFt = 24000,
            VerticalSpeedFpm = -1800,
        };
        // Unknown -> Descent carries the 5 s first-classification debounce (issue #59).
        var now = Drive(engine, airborneDescent, T0, TimeSpan.FromSeconds(6));
        Assert.Equal(FlightPhase.Descent, engine.CurrentPhase);

        // Level segment: target Cruise, but the 15 s Descent->Cruise debounce holds Descent
        // through a short level-off (the 2026-08-16 flip-flops were 20 s blips).
        var level = airborneDescent with { VerticalSpeedFpm = 0 };
        now = Drive(engine, level, now, TimeSpan.FromSeconds(13));
        Assert.Equal(FlightPhase.Descent, engine.CurrentPhase);

        Drive(engine, level, now, TimeSpan.FromSeconds(3));
        Assert.Equal(FlightPhase.Cruise, engine.CurrentPhase);
    }

    [Fact]
    public void ApproachToClimb_BlipHolds_SustainedGoAroundCommits()
    {
        // Issue #99 second layer: even a go-around-grade VS reading must sustain for the 5 s
        // Approach->Climb settle before committing — and a short blip never does.
        var engine = Create(out var session);
        SetSession(session, SimSessionPhase.InSession);

        var approach = new FlightDataSnapshot
        {
            IsValid = true,
            IsReady = true,
            OnGround = false,
            AircraftPowered = true,
            AnyEngineRunning = true,
            IndicatedAirspeedKt = 190,
            GroundSpeedKt = 215,
            RadioAltitudeFt = 3000,
            VerticalSpeedFpm = -600,
            GearDown = true,
        };
        var now = Drive(engine, approach, T0, TimeSpan.FromSeconds(6));
        Assert.Equal(FlightPhase.Approach, engine.CurrentPhase);

        // 3 s go-around-grade blip: evaluator votes Climb, the settle holds Approach.
        var climbBlip = approach with { VerticalSpeedFpm = 1500, GearDown = false };
        now = Drive(engine, climbBlip, now, TimeSpan.FromSeconds(3));
        Assert.Equal(FlightPhase.Approach, engine.CurrentPhase);
        now = Drive(engine, approach, now, TimeSpan.FromSeconds(2));
        Assert.Equal(FlightPhase.Approach, engine.CurrentPhase);

        // A real go-around sustains: commits after the settle window.
        Drive(engine, climbBlip, now, TimeSpan.FromSeconds(6));
        Assert.Equal(FlightPhase.Climb, engine.CurrentPhase);
    }

    [Fact]
    public void WarmupGarbage_AirborneAtZeroSpeed_IsHeldImplausible()
    {
        // The 2026-08-17 recurrence shape (issue #59): every ref has a first value, so
        // IsReady passes, but ProSim's own boot serves onGround=false with ias=0/gs=0 — the
        // sample is physically impossible and must never classify, let alone latch.
        var engine = Create(out var session);
        SetSession(session, SimSessionPhase.InSession);

        var warmupGarbage = new FlightDataSnapshot
        {
            IsValid = true,
            IsReady = true,
            OnGround = false,
            GearDown = true,
            RadioAltitudeFt = 0,
            IndicatedAirspeedKt = 0,
            GroundSpeedKt = 0,
            VerticalSpeedFpm = 0,
            AircraftPowered = false,
        };
        Drive(engine, warmupGarbage, T0, TimeSpan.FromSeconds(20));

        Assert.Equal(FlightPhase.Unknown, engine.CurrentPhase);
        Assert.False(engine.HasBeenAirborneThisSession);
    }

    [Fact]
    public void AirborneCommit_WithoutConvincingEvidence_DoesNotLatch()
    {
        // Plausible enough to classify (gs above the floor) but not convincingly airborne
        // (slow AND low): the phase may commit and self-correct later, but the write-safety
        // latch must hold — it is what opened the FOB restore on 2026-08-17.
        var engine = Create(out var session);
        SetSession(session, SimSessionPhase.InSession);

        var lowSlowApproach = new FlightDataSnapshot
        {
            IsValid = true,
            IsReady = true,
            OnGround = false,
            GearDown = true,
            RadioAltitudeFt = 150,
            IndicatedAirspeedKt = 40,
            GroundSpeedKt = 45,
            VerticalSpeedFpm = -400,
            AircraftPowered = true,
        };
        Drive(engine, lowSlowApproach, T0, TimeSpan.FromSeconds(6));

        Assert.Equal(FlightPhase.Approach, engine.CurrentPhase);
        Assert.False(engine.HasBeenAirborneThisSession);
    }

    [Fact]
    public void MidFlightRestart_StillClassifiesAndLatches_AfterTheFirstClassificationHold()
    {
        // App restart at cruise: genuine airborne data classifies after the 5 s
        // Unknown->airborne debounce and the latch opens — the hardening must not break the
        // legitimate restart path.
        var engine = Create(out var session);
        SetSession(session, SimSessionPhase.InSession);

        var cruise = new FlightDataSnapshot
        {
            IsValid = true,
            IsReady = true,
            OnGround = false,
            AircraftPowered = true,
            AnyEngineRunning = true,
            IndicatedAirspeedKt = 250,
            GroundSpeedKt = 440,
            RadioAltitudeFt = 30000,
            AltitudeFt = 36000,
            VerticalSpeedFpm = 0,
        };
        var now = Drive(engine, cruise, T0, TimeSpan.FromSeconds(4));
        Assert.Equal(FlightPhase.Unknown, engine.CurrentPhase);

        Drive(engine, cruise, now, TimeSpan.FromSeconds(2));
        Assert.Equal(FlightPhase.Cruise, engine.CurrentPhase);
        Assert.True(engine.HasBeenAirborneThisSession);
    }

    [Fact]
    public void AirborneLatch_SetsWhenAirborne_ResetsWhenSessionEnds()
    {
        var engine = Create(out var session);
        SetSession(session, SimSessionPhase.InSession);
        Assert.False(engine.HasBeenAirborneThisSession);

        var now = Drive(engine, ReadyGround(), T0, TimeSpan.FromSeconds(2));
        Assert.False(engine.HasBeenAirborneThisSession);

        var climb = new FlightDataSnapshot
        {
            IsValid = true,
            IsReady = true,
            OnGround = false,
            AircraftPowered = true,
            AnyEngineRunning = true,
            RadioAltitudeFt = 500,
            VerticalSpeedFpm = 2500,
            IndicatedAirspeedKt = 165,
        };
        now = Drive(engine, climb, now, TimeSpan.FromSeconds(1));
        Assert.True(engine.HasBeenAirborneThisSession);

        // Back to the menu: the latch must not leak into the next session.
        SetSession(session, SimSessionPhase.NotInSession);
        engine.ProcessTick(new FlightDataSnapshot { IsValid = false }, now);
        Assert.False(engine.HasBeenAirborneThisSession);
    }
}
