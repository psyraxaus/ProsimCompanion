using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Core.Weather;
using ProsimCompanion.Speech.Briefings;
using ProsimCompanion.Speech.Monitoring;
using Xunit;

namespace ProsimCompanion.Core.Tests.Weather;

/// <summary>Issue #182: wind components, the limit tables, the tones, the spoken verdict, the
/// runway-condition mappings, the once-per-approach advisory and the briefing clause.</summary>
public sealed class WindLimitsTests
{
    [Theory]
    [InlineData(270, 20, 270.0, 20.0, 0.0)]       // straight down the runway
    [InlineData(90, 20, 270.0, -20.0, 0.0)]       // straight tailwind
    [InlineData(360, 10, 0.0, 10.0, 0.0)]         // 360 and 000 are the same direction
    [InlineData(0, 10, 360.0, 10.0, 0.0)]
    [InlineData(300, 20, 270.0, 17.3, 10.0)]      // 30° off: cos/sin
    [InlineData(240, 20, 270.0, 17.3, 10.0)]      // the other side, same unsigned crosswind
    [InlineData(10, 20, 350.0, 18.8, 6.8)]        // across the north wrap
    [InlineData(180, 20, 270.0, 0.0, 20.0)]       // pure crosswind
    public void Components_AreCorrect_AcrossTheWrap(int dir, int speed, double heading, double headwind, double crosswind)
    {
        var wind = WindMath.Resolve(dir, speed, null, heading)!;

        Assert.Equal(headwind, wind.HeadwindKt, 1);
        Assert.Equal(crosswind, wind.CrosswindKt, 1);
        Assert.Null(wind.GustCrosswindKt);
        Assert.False(wind.DirectionUnknown);
    }

    [Fact]
    public void Gusts_AreASecondPair_AndTheWorstCaseIsGustIncluded()
    {
        var wind = WindMath.Resolve(300, 20, 35, 270)!;

        Assert.Equal(10.0, wind.CrosswindKt, 1);
        Assert.Equal(17.5, wind.GustCrosswindKt!.Value, 1);
        Assert.Equal(17.5, wind.MaxCrosswindKt, 1);
        Assert.Equal(0, wind.MaxTailwindKt);

        var tail = WindMath.Resolve(90, 8, 20, 270)!;
        Assert.Equal(8, tail.TailwindKt, 1);
        Assert.Equal(20, tail.MaxTailwindKt, 1);

        // A "gust" no stronger than the steady wind is not a gust.
        Assert.Null(WindMath.Resolve(300, 20, 20, 270)!.GustCrosswindKt);
    }

    [Fact]
    public void Calm_IsZeros_NoSpeedIsNull_AndVariableIsTheWorstCase()
    {
        Assert.Null(WindMath.Resolve(270, null, null, 270));
        var calm = WindMath.Resolve(null, 0, null, 270)!;
        Assert.Equal(0, calm.HeadwindKt);
        Assert.Equal(0, calm.CrosswindKt);

        var vrb = WindMath.Resolve(null, 12, 20, 270)!;
        Assert.True(vrb.DirectionUnknown);
        Assert.Equal(12, vrb.TailwindKt);
        Assert.Equal(12, vrb.CrosswindKt);
        Assert.Equal(20, vrb.MaxCrosswindKt);
        Assert.Equal(20, vrb.MaxTailwindKt);
    }

    [Theory]
    [InlineData(null, WindPhase.Takeoff, 38, "dry")]
    [InlineData(RunwayCondition.Dry, WindPhase.Landing, 38, "dry")]
    [InlineData(RunwayCondition.Wet, WindPhase.Landing, 33, "wet-runway")]
    [InlineData(RunwayCondition.Wet, WindPhase.Takeoff, 33, "wet-runway")]
    [InlineData(RunwayCondition.Rwycc5, WindPhase.Landing, 25, "RWYCC 5")]
    [InlineData(RunwayCondition.Rwycc4, WindPhase.Landing, 20, "RWYCC 4")]
    [InlineData(RunwayCondition.Rwycc3, WindPhase.Landing, 15, "RWYCC 3")]
    [InlineData(RunwayCondition.Rwycc2, WindPhase.Landing, 10, "RWYCC 2")]
    [InlineData(RunwayCondition.Rwycc1, WindPhase.Landing, 5, "RWYCC 1")]
    public void CrosswindLimit_FollowsTheConditionTable(RunwayCondition? condition, WindPhase phase, int expectedKt, string name)
    {
        var (kt, limitName) = WindLimitCheck.CrosswindLimit(new AircraftLimitsOptions(), phase, condition);
        Assert.Equal(expectedKt, kt);
        Assert.Equal(name, limitName);
    }

    [Theory]
    [InlineData(270, 10, null, WindLimitTone.Green, false, false)]     // 10 kt headwind
    [InlineData(180, 20, null, WindLimitTone.Green, false, false)]     // 20 kt crosswind vs 38
    [InlineData(180, 34, null, WindLimitTone.Amber, false, false)]     // within 5 kt of 38
    [InlineData(180, 39, null, WindLimitTone.Red, false, true)]        // beyond 38
    [InlineData(180, 30, 40, WindLimitTone.Red, false, true)]          // gust 40 beyond 38
    [InlineData(90, 11, null, WindLimitTone.Amber, false, false)]      // tailwind 11 vs 15
    [InlineData(90, 16, null, WindLimitTone.Red, true, false)]         // tailwind 16 beyond 15
    [InlineData(90, 15, null, WindLimitTone.Amber, false, false)]      // exactly the limit: not beyond
    public void Evaluate_TonesAndFlags_Landing_Dry(int dir, int speed, int? gust, WindLimitTone tone, bool tailExceeded, bool crossExceeded)
    {
        var wind = WindMath.Resolve(dir, speed, gust, 270)!;
        var verdict = WindLimitCheck.Evaluate(wind, new AircraftLimitsOptions(), WindPhase.Landing, RunwayCondition.Dry);

        Assert.Equal(tone, verdict.Tone);
        Assert.Equal(tailExceeded, verdict.TailwindExceeded);
        Assert.Equal(crossExceeded, verdict.CrosswindExceeded);
    }

    [Fact]
    public void Evaluate_WetAndContaminated_TightenTheCrosswind()
    {
        var wind = WindMath.Resolve(180, 35, null, 270)!;
        var limits = new AircraftLimitsOptions();

        Assert.Equal(WindLimitTone.Amber, WindLimitCheck.Evaluate(wind, limits, WindPhase.Landing, RunwayCondition.Dry).Tone);
        Assert.Equal(WindLimitTone.Red, WindLimitCheck.Evaluate(wind, limits, WindPhase.Landing, RunwayCondition.Wet).Tone);
        var rwycc3 = WindLimitCheck.Evaluate(WindMath.Resolve(180, 16, null, 270)!, limits, WindPhase.Landing, RunwayCondition.Rwycc3);
        Assert.True(rwycc3.CrosswindExceeded);
        Assert.Equal("RWYCC 3", rwycc3.CrosswindLimitName);
    }

    [Fact]
    public void Spoken_ReadsComponentsGustsAndTheVerdict()
    {
        var limits = new AircraftLimitsOptions();
        var within = WindLimitCheck.Evaluate(WindMath.Resolve(300, 28, null, 270)!, limits, WindPhase.Landing, RunwayCondition.Dry);
        Assert.Equal("Crosswind component 14 knots, headwind 24, within limits.", WindLimitCheck.Spoken(within));

        // 060/6 on runway 27: 150° off the nose — cos gives a 5 kt tailwind, sin a 3 kt crosswind.
        var tail = WindLimitCheck.Evaluate(WindMath.Resolve(60, 6, null, 270)!, limits, WindPhase.Landing, RunwayCondition.Dry);
        Assert.Equal("Crosswind component 3 knots, tailwind 5, within limits.", WindLimitCheck.Spoken(tail));

        var wet = WindLimitCheck.Evaluate(WindMath.Resolve(180, 36, 41, 270)!, limits, WindPhase.Landing, RunwayCondition.Wet);
        Assert.Equal("Crosswind component 36 knots, gusting 41, headwind 0 — exceeds the wet-runway crosswind limit of 33 knots.", WindLimitCheck.Spoken(wet));

        var tailExceeded = WindLimitCheck.Evaluate(WindMath.Resolve(90, 12, 18, 270)!, limits, WindPhase.Takeoff, null);
        Assert.Equal("Crosswind component 0 knots, tailwind 12, gusting 18 — exceeds the tailwind limit of 15 knots.", WindLimitCheck.Spoken(tailExceeded));

        var vrb = WindLimitCheck.Evaluate(WindMath.Resolve(null, 10, null, 270)!, limits, WindPhase.Landing, null);
        Assert.StartsWith("Variable wind 10 knots, taken as crosswind and tailwind", WindLimitCheck.Spoken(vrb), StringComparison.Ordinal);

        Assert.Equal("EXCEEDS WET-RUNWAY CROSSWIND LIMIT 33 KT", WindLimitCheck.Summary(wet));
        Assert.Equal("WITHIN LIMITS (XW 38 · TW 15)", WindLimitCheck.Summary(within));
        Assert.Contains(33, WindLimitCheck.Numbers(wet));
        Assert.Contains(41, WindLimitCheck.Numbers(wet));
    }

    [Theory]
    [InlineData(RunwayCondition.Dry, 6, 6, "dry")]
    [InlineData(RunwayCondition.Wet, 5, 5, "wet")]
    [InlineData(RunwayCondition.Rwycc5, 5, 5, "runway condition code 5")]
    [InlineData(RunwayCondition.Rwycc1, 1, 1, "runway condition code 1")]
    public void RunwayConditions_MapToTheCalculatorCodeAndTheSpokenName(RunwayCondition condition, int breakAction, int rwycc, string spoken)
    {
        Assert.Equal(breakAction, RunwayConditions.BreakAction(condition));
        Assert.Equal(rwycc, RunwayConditions.Rwycc(condition));
        Assert.Equal(spoken, RunwayConditions.Spoken(condition));
    }

    [Fact]
    public void RunwayConditionStore_ResetsWithTheFlightCycle()
    {
        var signals = new GroundOpsSignals();
        using var store = new RunwayConditionStore(signals);
        Assert.Null(store.Current);
        store.Set(RunwayCondition.Wet);
        Assert.Equal(RunwayCondition.Wet, store.Current);
        signals.RaiseFlightCycleReset();
        Assert.Null(store.Current);
    }

    [Fact]
    public void AdvisoryCore_SpeaksOncePerApproach_OnlyBeyondALimit_AndReArmsWhenThePhaseLeaves()
    {
        var core = new WindLimitAdvisoryCore();
        var limits = new AircraftLimitsOptions();
        var strong = new WxFacts("x", 180, 36, 9999, 1013, 15, null, null);
        var fine = new WxFacts("x", 270, 10, 9999, 1013, 15, null, null);

        Assert.Null(core.Observe(FlightPhase.Descent, strong, 270, RunwayCondition.Wet, limits));  // not on the approach
        Assert.Null(core.Observe(FlightPhase.Approach, fine, 270, RunwayCondition.Wet, limits));    // within limits: silent
        Assert.Null(core.Observe(FlightPhase.Approach, strong, null, RunwayCondition.Wet, limits)); // no heading: silent
        var said = core.Observe(FlightPhase.Approach, strong, 270, RunwayCondition.Wet, limits);
        Assert.NotNull(said);
        Assert.True(said.CrosswindExceeded);
        Assert.Null(core.Observe(FlightPhase.Approach, strong, 270, RunwayCondition.Wet, limits));  // once per approach

        core.Observe(FlightPhase.InitialClimb, strong, 270, RunwayCondition.Wet, limits);           // go-around re-arms
        Assert.NotNull(core.Observe(FlightPhase.Approach, strong, 270, RunwayCondition.Wet, limits));
    }

    [Fact]
    public void BriefingClause_FollowsTheWind_AndItsNumbersAreAllowed()
    {
        var nav = new NavDataFacts("2508", 10_000, 110, 270, 12_999, 21, "IMEA", 110.3, 3.0);
        var verdict = WindLimitCheck.Evaluate(WindMath.Resolve(180, 36, null, 270)!, new AircraftLimitsOptions(), WindPhase.Landing, RunwayCondition.Wet);
        var facts = new BriefingFacts(false, "YSSY", "27", null, null, null, nav, null, null, null, 180, 36, 1013, null, WindLimits: verdict);

        var keys = BriefingComposer.Sections(facts).Select(s => s.Key).ToArray();
        Assert.Equal(["wind", "wind-limits"], keys.SkipWhile(k => k != "wind").Take(2));
        Assert.Contains("Wind 180 at 36 knots. Crosswind component 36 knots, headwind 0 — exceeds the wet-runway crosswind limit of 33 knots.", BriefingComposer.Template(facts), StringComparison.Ordinal);
        Assert.Contains(33, BriefingComposer.AllowedNumbers(facts));
        Assert.DoesNotContain("wind-limits", BriefingComposer.Sections(facts with { WindLimits = null }).Select(s => s.Key));
        Assert.Contains("- Wind limits: crosswind 33 kt (wet-runway), tailwind 15 kt — EXCEEDED", BriefingComposer.FactBlock(facts), StringComparison.Ordinal);
    }
}
