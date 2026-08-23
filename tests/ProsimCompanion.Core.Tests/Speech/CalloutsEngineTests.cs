using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Callouts;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

public sealed class CalloutsEngineTests : IDisposable
{
    private readonly SopOptions _sop = new();
    private readonly FakeArbiter _arbiter = new();
    private readonly FakePhaseSource _phase = new();
    private readonly FakeFlightSource _source = new();
    private readonly ArrivalMinimaStore _minima = new();
    private readonly CalloutsEngine _engine;

    public CalloutsEngineTests()
    {
        _engine = new CalloutsEngine(
            SpeechTestSupport.SopMonitor(_sop),
            _arbiter,
            _source,
            _phase,
            _minima,
            SpeechTestSupport.TempEventLog(),
            NullLogger<CalloutsEngine>.Instance);
        _engine.Start(); // subscribes phase events; timer ticks are harmless (source invalid)
    }

    public void Dispose() => _engine.Dispose();

    private static FlightDataSnapshot Roll(double ias, int v1 = 140, int vr = 145, int v2 = 150)
        => new()
        {
            IsValid = true,
            OnGround = true,
            IndicatedAirspeedKt = ias,
            V1Kt = v1,
            VrKt = vr,
            V2Kt = v2,
            AltitudeFt = 1000,
        };

    [Fact]
    public void V1_FiresOnceAtV1_CriticalWithTag()
    {
        _phase.SetPhase(FlightPhase.TakeoffRoll);
        _engine.ProcessSample(Roll(120));
        _engine.ProcessSample(Roll(141));
        _engine.ProcessSample(Roll(142));

        var v1 = Assert.Single(_arbiter.Requests, r => r.Tag == "v1");
        Assert.Equal("V one", v1.Text);
        Assert.Equal(ProsimCompanion.Speech.Arbiter.SpeechPriority.Critical, v1.Priority);
    }

    [Fact]
    public void V1_NotEntered_StaysSilent()
    {
        _phase.SetPhase(FlightPhase.TakeoffRoll);
        _engine.ProcessSample(Roll(141, v1: 0));

        Assert.DoesNotContain(_arbiter.Requests, r => r.Tag == "v1");
    }

    [Fact]
    public void HundredKnots_And_Rotate_FireInSequence()
    {
        _phase.SetPhase(FlightPhase.TakeoffRoll);
        _engine.ProcessSample(Roll(90));
        _engine.ProcessSample(Roll(101));
        _engine.ProcessSample(Roll(146));

        Assert.Contains("hundredKnots", _arbiter.Tags);
        Assert.Contains("rotate", _arbiter.Tags);
    }

    [Fact]
    public void PositiveClimb_NeedsRisingRadioAcrossTwoSamples()
    {
        // A fresh TakeoffRoll arming clears the crossing history — with no prior sample the
        // first airborne pass records the baseline and deliberately cannot fire.
        _phase.SetPhase(FlightPhase.TakeoffRoll);
        _phase.SetPhase(FlightPhase.InitialClimb);

        _engine.ProcessSample(new FlightDataSnapshot
        {
            IsValid = true, OnGround = false, VerticalSpeedFpm = 800, RadioAltitudeFt = 20, AltitudeFt = 1100,
        });
        Assert.DoesNotContain("positiveClimb", _arbiter.Tags);

        _engine.ProcessSample(new FlightDataSnapshot
        {
            IsValid = true, OnGround = false, VerticalSpeedFpm = 800, RadioAltitudeFt = 60, AltitudeFt = 1200,
        });
        Assert.Contains("positiveClimb", _arbiter.Tags);
    }

    [Fact]
    public void AltitudeCallout_TenThousand_FiresBothDirections_OncePerDirectionLatch()
    {
        _phase.SetPhase(FlightPhase.Climb);
        _engine.ProcessSample(Climb(9_900));
        _engine.ProcessSample(Climb(10_050));

        Assert.Single(_arbiter.Requests, r => r.Tag == "altitude:10000");

        // Latched — crossing again (descent) stays quiet until re-armed.
        _engine.ProcessSample(Climb(10_100));
        _phase.SetPhase(FlightPhase.Descent);
        _engine.ProcessSample(Climb(9_900));
        Assert.Single(_arbiter.Requests, r => r.Tag == "altitude:10000");
    }

    private static FlightDataSnapshot Climb(double baro)
        => new() { IsValid = true, OnGround = false, AltitudeFt = baro, VerticalSpeedFpm = 1500 };

    [Fact]
    public void OneThousandToGo_FiresApproachingFcuAltitude()
    {
        _phase.SetPhase(FlightPhase.Climb);
        _engine.ProcessSample(new FlightDataSnapshot
        {
            IsValid = true, OnGround = false, AltitudeFt = 30_000, FcuAltitudeFt = 36_000, VerticalSpeedFpm = 2000,
        });
        Assert.DoesNotContain("oneThousandToGo", _arbiter.Tags);

        _engine.ProcessSample(new FlightDataSnapshot
        {
            IsValid = true, OnGround = false, AltitudeFt = 35_100, FcuAltitudeFt = 36_000, VerticalSpeedFpm = 2000,
        });
        Assert.Contains("oneThousandToGo", _arbiter.Tags);
    }

    [Fact]
    public void OneThousandToGo_WindingTheFcuKnob_DoesNotMachineGun()
    {
        // Issue #108, 2026-08-23 descent: spinning the FCU altitude knob made every 100 ms
        // sample a "genuinely different target" — 8 fires in 1.5 s. A moving knob must not
        // fire; the first settled sample fires once; the cooldown blocks re-fires.
        _phase.SetPhase(FlightPhase.Descent);
        FlightDataSnapshot At(double fcu) => new()
        {
            IsValid = true, OnGround = false, AltitudeFt = 7_000, FcuAltitudeFt = fcu, VerticalSpeedFpm = -900,
        };

        _engine.ProcessSample(At(7_500));
        _engine.ProcessSample(At(7_000));
        _engine.ProcessSample(At(6_500));
        _engine.ProcessSample(At(6_300));
        Assert.DoesNotContain("oneThousandToGo", _arbiter.Tags);

        // Knob stops: two consecutive samples on the same target — exactly one fire.
        _engine.ProcessSample(At(6_300));
        Assert.Single(_arbiter.Requests, r => r.Tag == "oneThousandToGo");

        // A new settled target inside the cooldown still stays quiet.
        _engine.ProcessSample(At(6_800));
        _engine.ProcessSample(At(6_800));
        Assert.Single(_arbiter.Requests, r => r.Tag == "oneThousandToGo");
    }

    [Fact]
    public void Spoilers_RolloutCommitRacingTheSampleTick_DoesNotDoubleFire()
    {
        // Issue #108: "spoilers" fired, the Approach->LandingRollout commit handler reset
        // the latch ~10 ms later, and the next tick fired it again. Rollout latches now
        // re-arm on Approach entry, so the commit is a no-op for them.
        _phase.SetPhase(FlightPhase.Approach);
        _phase.SetPhase(FlightPhase.LandingRollout);
        FlightDataSnapshot Rollout() => new()
        {
            IsValid = true, OnGround = true, IndicatedAirspeedKt = 130, GroundSpoilersDeployed = true,
        };

        _engine.ProcessSample(Rollout());
        _phase.SetPhase(FlightPhase.LandingRollout); // the racing commit event
        _engine.ProcessSample(Rollout());

        Assert.Single(_arbiter.Requests, r => r.Tag == "spoilers");
    }

    [Fact]
    public void Approach_OneThousand_FiveHundred_OnDescendingRadioCrossings()
    {
        _phase.SetPhase(FlightPhase.Approach);
        _engine.ProcessSample(Final(1200));
        _engine.ProcessSample(Final(950));
        _engine.ProcessSample(Final(520));
        _engine.ProcessSample(Final(480));

        Assert.Contains("oneThousand", _arbiter.Tags);
        Assert.Contains("fiveHundred", _arbiter.Tags);
    }

    private static FlightDataSnapshot Final(double radio, double baro = double.NaN)
        => new()
        {
            IsValid = true,
            OnGround = false,
            RadioAltitudeFt = radio,
            AltitudeFt = double.IsNaN(baro) ? radio + 500 : baro,
            VerticalSpeedFpm = -700,
        };

    [Fact]
    public void Minimums_WithoutEnteredMinima_StaysSilent()
    {
        _phase.SetPhase(FlightPhase.Approach);
        _engine.ProcessSample(Final(400));
        _engine.ProcessSample(Final(150));

        Assert.DoesNotContain("minimums", _arbiter.Tags);
        Assert.DoesNotContain("hundredAbove", _arbiter.Tags);
    }

    [Fact]
    public void Minimums_DecisionHeight_UsesRadioAltitude()
    {
        _minima.Set(new ArrivalMinima(ArrivalMinimumKind.DecisionHeight, 200));
        _phase.SetPhase(FlightPhase.Approach);
        _engine.ProcessSample(Final(400));
        _engine.ProcessSample(Final(290)); // through DH+100
        _engine.ProcessSample(Final(190)); // through DH

        Assert.Contains("hundredAbove", _arbiter.Tags);
        var minimums = Assert.Single(_arbiter.Requests, r => r.Tag == "minimums");
        Assert.Equal(ProsimCompanion.Speech.Arbiter.SpeechPriority.Critical, minimums.Priority);
    }

    [Fact]
    public void Minimums_DecisionAltitude_UsesBaroAltitude()
    {
        _minima.Set(new ArrivalMinima(ArrivalMinimumKind.DecisionAltitude, 1500));
        _phase.SetPhase(FlightPhase.Approach);
        _engine.ProcessSample(Final(radio: 2000, baro: 1700));
        _engine.ProcessSample(Final(radio: 1800, baro: 1550)); // through DA+100 (baro)
        _engine.ProcessSample(Final(radio: 1700, baro: 1490)); // through DA (baro)

        Assert.Contains("hundredAbove", _arbiter.Tags);
        Assert.Contains("minimums", _arbiter.Tags);
    }

    [Fact]
    public void Rollout_Spoilers_Reverse_Decel()
    {
        _phase.SetPhase(FlightPhase.LandingRollout);
        _engine.ProcessSample(new FlightDataSnapshot
        {
            IsValid = true, OnGround = true, IndicatedAirspeedKt = 130, GroundSpoilersDeployed = true,
        });
        _engine.ProcessSample(new FlightDataSnapshot
        {
            IsValid = true, OnGround = true, IndicatedAirspeedKt = 110,
            GroundSpoilersDeployed = true, ReversersMaxBoth = true,
        });
        _engine.ProcessSample(new FlightDataSnapshot
        {
            IsValid = true, OnGround = true, IndicatedAirspeedKt = 68, GroundSpoilersDeployed = true,
        });

        Assert.Contains("spoilers", _arbiter.Tags);
        Assert.Contains("reverseGreen", _arbiter.Tags);
        var decel = Assert.Single(_arbiter.Requests, r => r.Tag == "decelSpeed");
        Assert.Equal("seventy knots", decel.Text);
    }

    [Fact]
    public void InvalidSample_DoesNotUpdateCrossingHistory()
    {
        _phase.SetPhase(FlightPhase.Approach);
        _engine.ProcessSample(Final(1200));

        // Disconnect at 1200 ft; reconnect below the gate — no fabricated crossing… but the
        // retained prev (1200) still sees a legitimate descent continuation.
        _engine.ProcessSample(new FlightDataSnapshot { IsValid = false });
        _engine.ProcessSample(Final(950));

        Assert.Single(_arbiter.Requests, r => r.Tag == "oneThousand");
    }

    [Fact]
    public void NewTakeoffRoll_RearmsEverything()
    {
        _phase.SetPhase(FlightPhase.TakeoffRoll);
        _engine.ProcessSample(Roll(120));
        _engine.ProcessSample(Roll(141));
        Assert.Single(_arbiter.Requests, r => r.Tag == "v1");

        // Turnaround: park, then a fresh takeoff.
        _phase.SetPhase(FlightPhase.Preflight);
        _phase.SetPhase(FlightPhase.TakeoffRoll);
        _engine.ProcessSample(Roll(120));
        _engine.ProcessSample(Roll(141));

        Assert.Equal(2, _arbiter.Requests.Count(r => r.Tag == "v1"));
    }

    [Fact]
    public void GoAround_RearmsApproach_ButNotTakeoffLatches()
    {
        _phase.SetPhase(FlightPhase.Approach);
        _engine.ProcessSample(Final(1200));
        _engine.ProcessSample(Final(950));
        Assert.Single(_arbiter.Requests, r => r.Tag == "oneThousand");

        _phase.SetPhase(FlightPhase.InitialClimb); // go-around
        _phase.SetPhase(FlightPhase.Approach);
        _engine.ProcessSample(Final(1200));
        _engine.ProcessSample(Final(950));

        Assert.Equal(2, _arbiter.Requests.Count(r => r.Tag == "oneThousand"));
    }

    [Fact]
    public void FlapPlacard_Exceeded_FiresWithCooldown()
    {
        _phase.SetPhase(FlightPhase.Climb);
        var over = new FlightDataSnapshot
        {
            IsValid = true, OnGround = false, IndicatedAirspeedKt = 240, FlapHandle = 1, AltitudeFt = 4000,
        };
        _source.Snapshot = over; // validity predicates re-sample
        _engine.ProcessSample(over);
        _engine.ProcessSample(over); // within cooldown — no second advisory

        Assert.Single(_arbiter.Requests, r => r.Tag == "flapLimit");
    }

    [Fact]
    public void Disabled_Callout_DoesNotSpeak()
    {
        _sop.V1.Enabled = false;
        _phase.SetPhase(FlightPhase.TakeoffRoll);
        _engine.ProcessSample(Roll(120));
        _engine.ProcessSample(Roll(141));

        Assert.DoesNotContain("v1", _arbiter.Tags);
    }
}
