using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.Core.Configuration;
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
    public void IsLive_FollowsSessionAndReadiness_AndRaisesEdges()
    {
        // Issue #114: the FO's arming gate is the engine's classification verdict.
        var engine = Create(out var session);
        var edges = new List<bool>();
        engine.LiveChanged += edges.Add;

        engine.ProcessTick(ReadyGround(), T0);
        Assert.False(engine.IsLive);
        Assert.False(engine.Snapshot().IsLive);

        SetSession(session, SimSessionPhase.InSession);
        engine.ProcessTick(HalfRegisteredStartup(), T0 + Tick);
        Assert.False(engine.IsLive); // session live but data not ready

        engine.ProcessTick(ReadyGround(), T0 + Tick * 2);
        Assert.True(engine.IsLive);
        Assert.True(engine.Snapshot().IsLive);

        SetSession(session, SimSessionPhase.NotInSession);
        engine.ProcessTick(ReadyGround(), T0 + Tick * 3);
        Assert.False(engine.IsLive);

        Assert.Equal([true, false], edges);
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

        // A real go-around sustains: commits after the settle window — onto InitialClimb,
        // the edge the go-around consumers key on (review 2026-08-29); the climb-out to
        // Climb follows one default debounce later.
        Drive(engine, climbBlip, now, TimeSpan.FromSeconds(6));
        Assert.Equal(FlightPhase.InitialClimb, engine.CurrentPhase);
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

    // ---- 2026-08-29 robustness review ----

    private static FlightDataSnapshot TaxiingOut() => ReadyGround() with
    {
        AnyEngineRunning = true,
        ParkBrakeSet = false,
        GroundSpeedKt = 20,
        IndicatedAirspeedKt = 15,
    };

    [Fact]
    public void GroundContactFlicker_OneSample_NeverCommitsARunwayTransition()
    {
        // Prosim2GSX GroundTicks parity: a single not-on-ground sample (touchdown bounce,
        // SimConnect hiccup) must not commit the zero-debounce InitialClimb and fire its callouts.
        var engine = Create(out var session);
        SetSession(session, SimSessionPhase.InSession);
        var now = Drive(engine, TaxiingOut(), T0, TimeSpan.FromSeconds(2));
        Assert.Equal(FlightPhase.TaxiOut, engine.CurrentPhase);

        var airborneBlip = TaxiingOut() with { OnGround = false, IndicatedAirspeedKt = 60, RadioAltitudeFt = 5, VerticalSpeedFpm = 100 };
        engine.ProcessTick(airborneBlip, now);
        Assert.Equal(FlightPhase.TaxiOut, engine.CurrentPhase);
        engine.ProcessTick(TaxiingOut(), now + Tick);
        Assert.Equal(FlightPhase.TaxiOut, engine.CurrentPhase);

        // Two agreeing samples flip the committed ground state and the lift-off commits at once.
        engine.ProcessTick(airborneBlip, now + Tick * 2);
        engine.ProcessTick(airborneBlip, now + Tick * 3);
        Assert.Equal(FlightPhase.InitialClimb, engine.CurrentPhase);
    }

    [Fact]
    public void SessionEnd_ResetsThePhaseToUnknown_WithTheEngineRuleId()
    {
        var engine = Create(out var session);
        SetSession(session, SimSessionPhase.InSession);
        var now = Drive(engine, ReadyGround(), T0, TimeSpan.FromSeconds(2));
        Assert.Equal(FlightPhase.Preflight, engine.CurrentPhase);

        FlightPhaseChangedEventArgs? last = null;
        engine.PhaseChanged += (_, e) => last = e;
        SetSession(session, SimSessionPhase.NotInSession);
        engine.ProcessTick(ReadyGround(), now);

        Assert.Equal(FlightPhase.Unknown, engine.CurrentPhase);
        Assert.NotNull(last);
        Assert.Equal(FlightPhase.Preflight, last.Previous);
        Assert.Equal(FlightStateEngine.SessionEndedRuleId, last.RuleId);

        // The next session classifies afresh from Unknown.
        SetSession(session, SimSessionPhase.InSession);
        Drive(engine, ReadyGround(), now + Tick, TimeSpan.FromSeconds(2));
        Assert.Equal(FlightPhase.Preflight, engine.CurrentPhase);
    }

    [Fact]
    public void PhaseChanged_CarriesTheRuleIdAndReason()
    {
        var engine = Create(out var session);
        SetSession(session, SimSessionPhase.InSession);
        FlightPhaseChangedEventArgs? last = null;
        engine.PhaseChanged += (_, e) => last = e;

        Drive(engine, ReadyGround(), T0, TimeSpan.FromSeconds(2));

        Assert.NotNull(last);
        Assert.Equal("preflight", last.RuleId);
        Assert.False(string.IsNullOrWhiteSpace(last.Reason));
        Assert.Equal(last.Reason, engine.Snapshot().LastTransitionReason);
    }

    [Fact]
    public void ForcePhase_WithFreeze_HoldsAgainstEvidence_UntilResumed()
    {
        var engine = Create(out var session);
        SetSession(session, SimSessionPhase.InSession);
        var now = Drive(engine, ReadyGround(), T0, TimeSpan.FromSeconds(2));
        FlightPhaseChangedEventArgs? last = null;
        engine.PhaseChanged += (_, e) => last = e;

        engine.ForcePhase(FlightPhase.Cruise, freeze: true, "test");

        Assert.Equal(FlightPhase.Cruise, engine.CurrentPhase);
        Assert.True(engine.IsFrozen);
        Assert.True(engine.Snapshot().Frozen);
        Assert.Equal(FlightStateEngine.ManualOverrideRuleId, last?.RuleId);
        Assert.Contains("test", last?.Reason);
        // A forced airborne phase is an assertion, never evidence the aircraft flew.
        Assert.False(engine.HasBeenAirborneThisSession);

        now = Drive(engine, ReadyGround(), now, TimeSpan.FromSeconds(10));
        Assert.Equal(FlightPhase.Cruise, engine.CurrentPhase);

        engine.ResumeAutomatic("test");
        Assert.False(engine.IsFrozen);
        Drive(engine, ReadyGround(), now, TimeSpan.FromSeconds(2));
        Assert.Equal(FlightPhase.Preflight, engine.CurrentPhase); // spawn-recovery from a flight phase
    }

    [Fact]
    public void ForcePhase_WithoutFreeze_YieldsToTheNextEvidence()
    {
        var engine = Create(out var session);
        SetSession(session, SimSessionPhase.InSession);
        var now = Drive(engine, ReadyGround(), T0, TimeSpan.FromSeconds(2));

        engine.ForcePhase(FlightPhase.TaxiOut, freeze: false, "test");
        Assert.Equal(FlightPhase.TaxiOut, engine.CurrentPhase);

        // Engines-off evidence walks it back via the 5 s departure-regression settle.
        Drive(engine, ReadyGround(), now, TimeSpan.FromSeconds(6));
        Assert.Equal(FlightPhase.Preflight, engine.CurrentPhase);
    }

    [Fact]
    public void SessionEnd_ReleasesAFrozenOverride()
    {
        var engine = Create(out var session);
        SetSession(session, SimSessionPhase.InSession);
        var now = Drive(engine, ReadyGround(), T0, TimeSpan.FromSeconds(2));
        engine.ForcePhase(FlightPhase.Cruise, freeze: true, "test");

        SetSession(session, SimSessionPhase.NotInSession);
        engine.ProcessTick(ReadyGround(), now);

        Assert.False(engine.IsFrozen);
        Assert.Equal(FlightPhase.Unknown, engine.CurrentPhase);
    }

    // ---- MSFS pause (issue #168, EGCC 2026-10-11 23:35Z) ----

    /// <summary>The Escape menu as SimSessionService reports it: camera 34, sim running,
    /// pause flag set.</summary>
    private static void SetPausedOutOfSession(SimSessionStore store)
        => store.Publish(new SimSessionSnapshot(SimSessionPhase.NotInSession, SimRunning: true, Paused: true, CameraState: 34, SimVersion: null));

    private static readonly GeoPoint Stand = GeoPoint.FromRaw(53.3537, -2.2750)!.Value;

    /// <summary>Powered at the gate with GSX boarding latched — the Departure the pause must keep.</summary>
    private static FlightDataSnapshot AtStand(double fuelKg = 6200, GeoPoint? position = null)
        => ReadyGround() with { Position = position ?? Stand, FuelOnBoardKg = fuelKg };

    private static FlightStateEngine CreateWithGroundOps(out SimSessionStore session, out GroundOpsSignals groundOps)
    {
        session = new SimSessionStore();
        groundOps = new GroundOpsSignals();
        return new FlightStateEngine(
            new NullFlightSource(), session, NullLogger<FlightStateEngine>.Instance,
            new FixedOptionsMonitor<FlightStateOptions>(FlightStateOptions.Default), groundOps);
    }

    [Fact]
    public void Pause_WithinTheGrace_KeepsThePhaseAndLatches_AndRaisesPausedThenResumed()
    {
        var engine = CreateWithGroundOps(out var session, out var groundOps);
        SetSession(session, SimSessionPhase.InSession);
        var now = Drive(engine, AtStand(), T0, TimeSpan.FromSeconds(2));
        groundOps.RaiseBoardingStarted();
        now = Drive(engine, AtStand(), now, TimeSpan.FromSeconds(2));
        Assert.Equal(FlightPhase.Departure, engine.CurrentPhase);

        var edges = new List<FlightPhaseChangedEventArgs>();
        var pauses = new List<FlightPauseChange>();
        var resets = 0;
        var live = new List<bool>();
        engine.PhaseChanged += (_, e) => edges.Add(e);
        engine.PauseChanged += pauses.Add;
        engine.SessionReset += () => resets++;
        engine.LiveChanged += live.Add;

        // Escape: five seconds in the pause menu, ProSim keeps pushing the same sample.
        SetPausedOutOfSession(session);
        now = Drive(engine, AtStand(), now, TimeSpan.FromSeconds(5));
        Assert.True(engine.IsPaused);
        Assert.False(engine.IsLive);
        Assert.Equal(FlightPhase.Departure, engine.CurrentPhase);

        SetSession(session, SimSessionPhase.InSession);
        now = Drive(engine, AtStand(), now, TimeSpan.FromSeconds(3));

        Assert.False(engine.IsPaused);
        Assert.True(engine.IsLive);
        Assert.Equal(FlightPhase.Departure, engine.CurrentPhase);
        Assert.Empty(edges); // no session-ended, no Unknown -> Preflight
        Assert.Equal(0, resets);
        Assert.Equal([FlightPauseEvent.Paused, FlightPauseEvent.Resumed], pauses.Select(p => p.Kind));
        Assert.Equal([false, true], live);
        Assert.Contains("within 50 m", pauses[1].Detail);
        Assert.Contains("within 50 kg", pauses[1].Detail);
        Assert.True(engine.Snapshot().Data!.BoardingStarted, "the boarding latch must survive the pause");
    }

    [Fact]
    public void Pause_KeepsTheAirborneLatch()
    {
        var engine = Create(out var session);
        SetSession(session, SimSessionPhase.InSession);
        var cruise = new FlightDataSnapshot
        {
            IsValid = true,
            IsReady = true,
            OnGround = false,
            AircraftPowered = true,
            AnyEngineRunning = true,
            RadioAltitudeFt = 30000,
            AltitudeFt = 36000,
            IndicatedAirspeedKt = 270,
            FmsCruiseAltFt = 36000,
            Position = Stand,
            FuelOnBoardKg = 5000,
        };
        var now = Drive(engine, cruise, T0, TimeSpan.FromSeconds(12));
        Assert.Equal(FlightPhase.Cruise, engine.CurrentPhase);
        Assert.True(engine.HasBeenAirborneThisSession);

        SetPausedOutOfSession(session);
        now = Drive(engine, cruise, now, TimeSpan.FromSeconds(20));
        SetSession(session, SimSessionPhase.InSession);
        Drive(engine, cruise, now, TimeSpan.FromSeconds(2));

        Assert.True(engine.HasBeenAirborneThisSession);
        Assert.Equal(FlightPhase.Cruise, engine.CurrentPhase);
    }

    [Fact]
    public void Pause_LongerThanTheGrace_IsASessionEnd()
    {
        var engine = Create(out var session);
        SetSession(session, SimSessionPhase.InSession);
        var now = Drive(engine, AtStand(), T0, TimeSpan.FromSeconds(2));
        Assert.Equal(FlightPhase.Preflight, engine.CurrentPhase);

        var pauses = new List<FlightPauseChange>();
        var resets = 0;
        FlightPhaseChangedEventArgs? last = null;
        engine.PauseChanged += pauses.Add;
        engine.SessionReset += () => resets++;
        engine.PhaseChanged += (_, e) => last = e;

        SetPausedOutOfSession(session);
        Drive(engine, AtStand(), now, TimeSpan.FromSeconds(61));

        Assert.False(engine.IsPaused);
        Assert.Equal(FlightPhase.Unknown, engine.CurrentPhase);
        Assert.Equal(FlightStateEngine.SessionEndedRuleId, last?.RuleId);
        Assert.Equal(1, resets); // one edge, however long NotInSession persists
        Assert.Equal([FlightPauseEvent.Paused, FlightPauseEvent.Ended], pauses.Select(p => p.Kind));
        Assert.Contains("longer than the 60 s grace", pauses[1].Detail);
    }

    [Fact]
    public void Pause_ThatComesBackElsewhere_IsARespawn()
    {
        var engine = Create(out var session);
        SetSession(session, SimSessionPhase.InSession);
        var now = Drive(engine, AtStand(), T0, TimeSpan.FromSeconds(2));

        var pauses = new List<FlightPauseChange>();
        engine.PauseChanged += pauses.Add;
        SetPausedOutOfSession(session);
        now = Drive(engine, AtStand(), now, TimeSpan.FromSeconds(10));
        SetSession(session, SimSessionPhase.InSession);

        // Same fuel, but a stand 300 m away: a new flight loaded at the next gate.
        var elsewhere = AtStand(position: GeoPoint.FromRaw(53.3564, -2.2750));
        engine.ProcessTick(elsewhere, now);

        Assert.False(engine.IsPaused);
        Assert.Equal(FlightPhase.Unknown, engine.CurrentPhase);
        Assert.Equal(FlightPauseEvent.Ended, pauses[^1].Kind);
        Assert.StartsWith("respawn", pauses[^1].Detail);
        Assert.Contains("beyond 50 m", pauses[^1].Detail);

        // …and classification continues from Unknown in the new session.
        Drive(engine, elsewhere, now + Tick, TimeSpan.FromSeconds(2));
        Assert.Equal(FlightPhase.Preflight, engine.CurrentPhase);
    }

    [Fact]
    public void Pause_ThatComesBackWithOtherFuel_IsARespawn()
    {
        var engine = Create(out var session);
        SetSession(session, SimSessionPhase.InSession);
        var now = Drive(engine, AtStand(fuelKg: 6200), T0, TimeSpan.FromSeconds(2));

        SetPausedOutOfSession(session);
        now = Drive(engine, AtStand(fuelKg: 6200), now, TimeSpan.FromSeconds(10));
        SetSession(session, SimSessionPhase.InSession);
        engine.ProcessTick(AtStand(fuelKg: 9800), now);

        Assert.Equal(FlightPhase.Unknown, engine.CurrentPhase);
    }

    [Fact]
    public void Pause_WithoutPositionOrFuel_TrustsTheGrace()
    {
        // Older data shape (no position ref, fuel not reported): the comparison is skipped and
        // the round-trip within the grace still resumes.
        var engine = Create(out var session);
        SetSession(session, SimSessionPhase.InSession);
        var now = Drive(engine, ReadyGround(), T0, TimeSpan.FromSeconds(2));

        var pauses = new List<FlightPauseChange>();
        engine.PauseChanged += pauses.Add;
        SetPausedOutOfSession(session);
        now = Drive(engine, ReadyGround(), now, TimeSpan.FromSeconds(5));
        SetSession(session, SimSessionPhase.InSession);
        engine.ProcessTick(ReadyGround(), now);

        Assert.Equal(FlightPhase.Preflight, engine.CurrentPhase);
        Assert.Equal(FlightPauseEvent.Resumed, pauses[^1].Kind);
        Assert.Contains("no position to compare", pauses[^1].Detail);
        Assert.Contains("no fuel to compare", pauses[^1].Detail);
    }

    [Fact]
    public void NotInSession_WithoutThePauseFlag_ResetsAtOnce()
    {
        // World map / aircraft reload: the pre-#168 behaviour, no pause events at all.
        var engine = Create(out var session);
        SetSession(session, SimSessionPhase.InSession);
        var now = Drive(engine, AtStand(), T0, TimeSpan.FromSeconds(2));

        var pauses = new List<FlightPauseChange>();
        engine.PauseChanged += pauses.Add;
        SetSession(session, SimSessionPhase.NotInSession); // Paused: false
        engine.ProcessTick(AtStand(), now);

        Assert.Equal(FlightPhase.Unknown, engine.CurrentPhase);
        Assert.Empty(pauses);
    }

    [Fact]
    public void Pause_GraceZero_ResetsAtOnce()
    {
        var session = new SimSessionStore();
        var engine = new FlightStateEngine(
            new NullFlightSource(), session, NullLogger<FlightStateEngine>.Instance,
            new FixedOptionsMonitor<FlightStateOptions>(new FlightStateOptions { PauseGraceSeconds = 0 }));
        SetSession(session, SimSessionPhase.InSession);
        var now = Drive(engine, AtStand(), T0, TimeSpan.FromSeconds(2));

        SetPausedOutOfSession(session);
        engine.ProcessTick(AtStand(), now);

        Assert.False(engine.IsPaused);
        Assert.Equal(FlightPhase.Unknown, engine.CurrentPhase);
    }

    [Fact]
    public void GenuineReset_MidBoarding_ReRaisesTheBoardingLatch_SoPreflightStepsOnToDeparture()
    {
        var engine = CreateWithGroundOps(out var session, out var groundOps);
        SetSession(session, SimSessionPhase.InSession);
        var now = Drive(engine, AtStand(), T0, TimeSpan.FromSeconds(2));
        groundOps.RaiseBoardingStarted();
        now = Drive(engine, AtStand(), now, TimeSpan.FromSeconds(2));
        Assert.Equal(FlightPhase.Departure, engine.CurrentPhase);

        // Out to the world map (no pause flag) while GSX is still boarding.
        var edges = new List<string>();
        engine.PhaseChanged += (_, e) => edges.Add($"{e.Previous} -> {e.Current}");
        SetSession(session, SimSessionPhase.NotInSession);
        engine.ProcessTick(AtStand(), now);
        Assert.Equal(FlightPhase.Unknown, engine.CurrentPhase);

        SetSession(session, SimSessionPhase.InSession);
        Drive(engine, AtStand(), now + Tick, TimeSpan.FromSeconds(4));

        Assert.Equal(FlightPhase.Departure, engine.CurrentPhase);
        Assert.Equal(["Departure -> Unknown", "Unknown -> Preflight", "Preflight -> Departure"], edges);
    }

    [Fact]
    public void GenuineReset_AfterBoardingCompleted_DoesNotReRaiseTheLatch()
    {
        var engine = CreateWithGroundOps(out var session, out var groundOps);
        SetSession(session, SimSessionPhase.InSession);
        var now = Drive(engine, AtStand(), T0, TimeSpan.FromSeconds(2));
        groundOps.RaiseBoardingStarted();
        groundOps.RaiseBoardingCompleted();
        groundOps.RaiseFlightCycleReset(); // next leg, nothing boarding

        SetSession(session, SimSessionPhase.NotInSession);
        engine.ProcessTick(AtStand(), now);
        SetSession(session, SimSessionPhase.InSession);
        Drive(engine, AtStand(), now + Tick, TimeSpan.FromSeconds(4));

        Assert.Equal(FlightPhase.Preflight, engine.CurrentPhase);
    }

    [Fact]
    public void IsSameFlight_ComparesPositionAndFuel_SkippingWhatIsUnknown()
    {
        var options = FlightStateOptions.Default;
        var before = AtStand(fuelKg: 6200);

        Assert.True(FlightStateEngine.IsSameFlight(before, AtStand(fuelKg: 6230), options, out var detail));
        Assert.Contains("moved 0 m", detail);

        Assert.False(FlightStateEngine.IsSameFlight(before, AtStand(fuelKg: 6300), options, out detail));
        Assert.Contains("fuel changed 100 kg (beyond 50 kg)", detail);

        Assert.True(FlightStateEngine.IsSameFlight(null, AtStand(), options, out detail));
        Assert.Equal("no position to compare, no fuel to compare", detail);
    }

    [Fact]
    public void Shutdown_ParkedWithBeaconOff_TurnsAroundAfterTheHold_OnceTheArrivalIsComplete()
    {
        var engine = Create(out var session);
        SetSession(session, SimSessionPhase.InSession);
        var now = Drive(engine, ReadyGround(), T0, TimeSpan.FromSeconds(2));
        engine.ForcePhase(FlightPhase.Shutdown, freeze: false, "test");

        // Still deboarding: parked for far longer than the hold changes nothing (2026-08-29 ESSA).
        var hold = TimeSpan.FromSeconds(FlightStateOptions.Default.TurnaroundHoldSeconds);
        now = Drive(engine, ReadyGround(), now, hold * 3);
        Assert.Equal(FlightPhase.Shutdown, engine.CurrentPhase);

        engine.NotifyArrivalComplete();
        now = Drive(engine, ReadyGround(), now, hold - TimeSpan.FromSeconds(2));
        Assert.Equal(FlightPhase.Shutdown, engine.CurrentPhase);

        Drive(engine, ReadyGround(), now, TimeSpan.FromSeconds(3));
        Assert.Equal(FlightPhase.Preflight, engine.CurrentPhase);
    }

    [Fact]
    public void ArrivalComplete_OutsideShutdown_IsIgnored_AndTheLatchDiesWithTheShutdown()
    {
        var engine = Create(out var session);
        SetSession(session, SimSessionPhase.InSession);
        var now = Drive(engine, ReadyGround(), T0, TimeSpan.FromSeconds(2));

        engine.NotifyArrivalComplete(); // Preflight — nothing to arm
        engine.ForcePhase(FlightPhase.Shutdown, freeze: false, "test");
        Drive(engine, ReadyGround(), now, TimeSpan.FromSeconds(FlightStateOptions.Default.TurnaroundHoldSeconds + 2));

        Assert.Equal(FlightPhase.Shutdown, engine.CurrentPhase);
    }

    [Fact]
    public void GroundOpsSignal_ArmsTheTurnaround()
    {
        var session = new SimSessionStore();
        var signals = new GroundOpsSignals();
        using var engine = new FlightStateEngine(
            new NullFlightSource(), session, NullLogger<FlightStateEngine>.Instance,
            new FixedOptionsMonitor<FlightStateOptions>(new FlightStateOptions { TurnaroundHoldSeconds = 2 }), signals);
        SetSession(session, SimSessionPhase.InSession);
        var now = Drive(engine, ReadyGround(), T0, TimeSpan.FromSeconds(2));
        engine.ForcePhase(FlightPhase.Shutdown, freeze: false, "test");

        signals.RaiseArrivalCompleted();
        Drive(engine, ReadyGround(), now, TimeSpan.FromSeconds(3));

        Assert.Equal(FlightPhase.Preflight, engine.CurrentPhase);
    }

    [Fact]
    public void Options_AreReadLive_FromTheMonitor()
    {
        // Hot-reloadable thresholds: a longer turnaround hold from the options monitor is
        // honoured on the next tick without restarting the engine.
        var session = new SimSessionStore();
        var options = new FlightStateOptions { TurnaroundHoldSeconds = 2 };
        var engine = new FlightStateEngine(
            new NullFlightSource(), session, NullLogger<FlightStateEngine>.Instance,
            new FixedOptionsMonitor<FlightStateOptions>(options));
        SetSession(session, SimSessionPhase.InSession);
        var now = Drive(engine, ReadyGround(), T0, TimeSpan.FromSeconds(2));
        engine.ForcePhase(FlightPhase.Shutdown, freeze: false, "test");
        engine.NotifyArrivalComplete();

        Drive(engine, ReadyGround(), now, TimeSpan.FromSeconds(3));

        Assert.Equal(FlightPhase.Preflight, engine.CurrentPhase);
    }

    // ---- Departure latch (owner decision 2026-09-20) ----

    [Fact]
    public void GroundOpsSignal_BoardingStarted_TakesPreflightToDeparture()
    {
        var session = new SimSessionStore();
        var signals = new GroundOpsSignals();
        using var engine = new FlightStateEngine(
            new NullFlightSource(), session, NullLogger<FlightStateEngine>.Instance,
            new FixedOptionsMonitor<FlightStateOptions>(FlightStateOptions.Default), signals);
        SetSession(session, SimSessionPhase.InSession);
        var now = Drive(engine, ReadyGround(), T0, TimeSpan.FromSeconds(2));
        Assert.Equal(FlightPhase.Preflight, engine.CurrentPhase);

        signals.RaiseBoardingStarted();
        now = Drive(engine, ReadyGround(), now, TimeSpan.FromSeconds(2));
        Assert.Equal(FlightPhase.Departure, engine.CurrentPhase);

        // Boarding complete, doors closed, beacon on with the brake still set: still Departure.
        now = Drive(engine, ReadyGround() with { BeaconOn = true, ApuRunning = true }, now, TimeSpan.FromSeconds(10));
        Assert.Equal(FlightPhase.Departure, engine.CurrentPhase);

        // Brake released with the beacon and APU: the push.
        Drive(engine, ReadyGround() with { BeaconOn = true, ApuRunning = true, ParkBrakeSet = false }, now, TimeSpan.FromSeconds(2));
        Assert.Equal(FlightPhase.PushbackAndStart, engine.CurrentPhase);
    }

    [Fact]
    public void BoardingStarted_BeforeTheFirstClassification_IsKept()
    {
        // A boarding that starts while the cockpit is unpowered (or before the engine is
        // live) must still yield Departure once Preflight is reached.
        var engine = Create(out var session);
        engine.NotifyBoardingStarted(); // Unknown
        SetSession(session, SimSessionPhase.InSession);

        Drive(engine, ReadyGround(), T0, TimeSpan.FromSeconds(4));

        Assert.Equal(FlightPhase.Departure, engine.CurrentPhase);
    }

    [Fact]
    public void BoardingStarted_AfterTaxiOut_IsIgnored()
    {
        var engine = Create(out var session);
        SetSession(session, SimSessionPhase.InSession);
        var now = Drive(engine, ReadyGround(), T0, TimeSpan.FromSeconds(2));
        engine.ForcePhase(FlightPhase.TaxiIn, freeze: false, "test");

        engine.NotifyBoardingStarted(); // arrival side — not the next departure
        engine.ForcePhase(FlightPhase.Preflight, freeze: false, "test");
        Drive(engine, ReadyGround(), now, TimeSpan.FromSeconds(4));

        Assert.Equal(FlightPhase.Preflight, engine.CurrentPhase);
    }

    [Fact]
    public void BoardingLatch_DiesWhenTheAircraftLeavesTheGate()
    {
        var engine = Create(out var session);
        SetSession(session, SimSessionPhase.InSession);
        var now = Drive(engine, ReadyGround(), T0, TimeSpan.FromSeconds(2));
        engine.NotifyBoardingStarted();
        now = Drive(engine, ReadyGround(), now, TimeSpan.FromSeconds(2));
        Assert.Equal(FlightPhase.Departure, engine.CurrentPhase);

        engine.ForcePhase(FlightPhase.TaxiOut, freeze: false, "test"); // latch cleared here
        engine.ForcePhase(FlightPhase.Preflight, freeze: false, "test");
        Drive(engine, ReadyGround(), now, TimeSpan.FromSeconds(4));

        Assert.Equal(FlightPhase.Preflight, engine.CurrentPhase);
    }
}
