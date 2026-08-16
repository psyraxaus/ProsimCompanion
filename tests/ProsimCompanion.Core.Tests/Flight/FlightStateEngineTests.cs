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
            RadioAltitudeFt = 20000,
            AltitudeFt = 24000,
            VerticalSpeedFpm = -1800,
        };
        var now = Drive(engine, airborneDescent, T0, TimeSpan.FromSeconds(2));
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
