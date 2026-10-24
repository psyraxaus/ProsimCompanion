using ProsimCompanion.Core.Flight;
using Xunit;

namespace ProsimCompanion.Core.Tests.Flight;

/// <summary>Issue #186: the detector's tables — unit from the at-rest baseline, step and spike,
/// hysteresis on the way down, the minimum gap between changes, the episode tail.</summary>
public sealed class TurbulenceDetectorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 24, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(1.0, AccelerationUnit.G)]
    [InlineData(-0.98, AccelerationUnit.G)]
    [InlineData(9.81, AccelerationUnit.MetresPerSecondSquared)]
    [InlineData(32.2, AccelerationUnit.FeetPerSecondSquared)]
    [InlineData(0.01, AccelerationUnit.GWithoutGravity)]
    [InlineData(4.0, AccelerationUnit.Unknown)]
    public void Baseline_NamesTheUnit(double baseline, AccelerationUnit expected)
    {
        Assert.Equal(expected, TurbulenceDetector.GuessUnit(baseline));
    }

    [Fact]
    public void AtRest_LearnsTheBaseline_AndTheUnit_FromFiftySamples()
    {
        var d = new TurbulenceDetector();
        Park(d, raw: 9.81, samples: 49);
        Assert.False(d.Calibrated);
        Park(d, raw: 9.81, samples: 1, startAt: 49);
        Assert.True(d.Calibrated);
        Assert.Equal(9.81, d.BaselineRaw!.Value, 3);
        Assert.Equal(AccelerationUnit.MetresPerSecondSquared, d.Unit);
    }

    [Fact]
    public void ForcedUnit_WinsOverTheGuess()
    {
        var d = new TurbulenceDetector(forcedUnit: AccelerationUnit.FeetPerSecondSquared);
        Park(d, raw: 1.0, samples: 50);
        Assert.Equal(AccelerationUnit.FeetPerSecondSquared, d.Unit);
    }

    [Fact]
    public void Step_InMetresPerSecondSquared_IsJudgedInG()
    {
        // 9.81 at rest = m/s²; a 0.3 g bump is about 2.9 m/s² of deviation.
        var d = new TurbulenceDetector();
        Park(d, raw: 9.81, samples: 50);
        var t = Fly(d, raw: 9.81, seconds: 3, from: 50);
        Assert.Equal(TurbulenceLevel.None, d.Level);

        var step = d.Process(new TurbulenceSample(t, 9.81 + 2.9, false, 450, true));
        Assert.True(step.LevelChanged);
        Assert.Equal(TurbulenceLevel.Light, d.Level);
        Assert.InRange(d.PeakG, 0.28, 0.31);
    }

    [Fact]
    public void Spike_ThenCalm_StaysLightForTheWindow_ThenNeedsHysteresisAndTheGap()
    {
        var d = new TurbulenceDetector();
        Park(d, raw: 1.0, samples: 50);
        var t = Fly(d, raw: 1.0, seconds: 3, from: 50);
        d.Process(new TurbulenceSample(t, 1.6, false, 450, true));
        Assert.Equal(TurbulenceLevel.Moderate, d.Level);

        // 1.9 s later the spike is still inside the window.
        t = Fly(d, raw: 1.0, seconds: 1.9, from: 0, start: t.AddMilliseconds(100));
        Assert.Equal(TurbulenceLevel.Moderate, d.Level);

        // 30 s of calm: the window is clear (peak 0 < 0.7 × 0.5) but the 60 s gap holds the level.
        t = Fly(d, raw: 1.0, seconds: 30, from: 0, start: t.AddMilliseconds(100));
        Assert.Equal(TurbulenceLevel.Moderate, d.Level);

        // Past the gap it drops.
        Fly(d, raw: 1.0, seconds: 31, from: 0, start: t.AddMilliseconds(100));
        Assert.Equal(TurbulenceLevel.None, d.Level);
    }

    [Fact]
    public void Hysteresis_HoldsTheLevel_WhileTheMetricHoversJustUnderTheBand()
    {
        var d = new TurbulenceDetector();
        Park(d, raw: 1.0, samples: 50);
        var t = Fly(d, raw: 1.0, seconds: 3, from: 50);
        d.Process(new TurbulenceSample(t, 1.25, false, 450, true)); // 0.25 g → Light
        Assert.Equal(TurbulenceLevel.Light, d.Level);

        // 70 s of 0.16 g bumps: under the 0.2 threshold but over 0.7 × 0.2 = 0.14 → stays Light.
        var at = t.AddMilliseconds(100);
        for (var i = 0; i < 700; i++, at = at.AddMilliseconds(100))
        {
            d.Process(new TurbulenceSample(at, i % 2 == 0 ? 1.16 : 1.0, false, 450, true));
        }

        Assert.Equal(TurbulenceLevel.Light, d.Level);

        // 0.10 g bumps are clear of the band → None (the gap has long passed).
        for (var i = 0; i < 30; i++, at = at.AddMilliseconds(100))
        {
            d.Process(new TurbulenceSample(at, i % 2 == 0 ? 1.10 : 1.0, false, 450, true));
        }

        Assert.Equal(TurbulenceLevel.None, d.Level);
    }

    [Fact]
    public void Episode_EndsTwoMinutesBelowLight_Once()
    {
        var d = new TurbulenceDetector();
        Park(d, raw: 1.0, samples: 50);
        var t = Fly(d, raw: 1.0, seconds: 3, from: 50);
        d.Process(new TurbulenceSample(t, 1.3, false, 450, true));
        Assert.True(d.EpisodeActive);
        Assert.Equal(t, d.EpisodeStartedAtUtc);

        var ended = 0;
        var at = t.AddMilliseconds(100);
        for (var i = 0; i < 2000; i++, at = at.AddMilliseconds(100))
        {
            if (d.Process(new TurbulenceSample(at, 1.0, false, 450, true)).EpisodeEnded)
            {
                ended++;
            }
        }

        // Level drops at 60 s (gap); the episode ends 120 s after that.
        Assert.Equal(1, ended);
        Assert.False(d.EpisodeActive);
        Assert.Equal(TurbulenceLevel.None, d.Level);
    }

    [Fact]
    public void OnTheGround_NeverJudgesALevel_AndBeingAirborneWithoutBaselineUsesTheFlightAverage()
    {
        var d = new TurbulenceDetector();
        // Taxi bumps: airborne false → no level, whatever the metric.
        var at = T0;
        for (var i = 0; i < 40; i++, at = at.AddMilliseconds(100))
        {
            d.Process(new TurbulenceSample(at, i % 2 == 0 ? 1.8 : 1.0, true, 15, false));
        }

        Assert.Equal(TurbulenceLevel.None, d.Level);
        Assert.False(d.Calibrated);

        // Started airborne (app launched in the cruise): the slow average becomes the baseline.
        at = Fly(d, raw: 1.0, seconds: 5, from: 0, start: at);
        Assert.False(d.Calibrated);
        Assert.NotNull(d.BaselineRaw);
        Assert.Equal(AccelerationUnit.G, d.Unit);
        d.Process(new TurbulenceSample(at, 1.7, false, 450, true));
        Assert.Equal(TurbulenceLevel.Moderate, d.Level);
    }

    [Fact]
    public void NaN_IsIgnored_NotAStep()
    {
        var d = new TurbulenceDetector();
        Park(d, raw: 1.0, samples: 50);
        var t = Fly(d, raw: 1.0, seconds: 3, from: 50);
        var step = d.Process(new TurbulenceSample(t, double.NaN, false, 450, true));
        Assert.False(step.LevelChanged);
        Assert.Equal(TurbulenceLevel.None, d.Level);
    }

    [Theory]
    [InlineData("auto", AccelerationUnit.Unknown)]
    [InlineData("G", AccelerationUnit.G)]
    [InlineData("gNoGravity", AccelerationUnit.GWithoutGravity)]
    [InlineData("mps2", AccelerationUnit.MetresPerSecondSquared)]
    [InlineData("ftps2", AccelerationUnit.FeetPerSecondSquared)]
    [InlineData("", AccelerationUnit.Unknown)]
    public void UnitOption_Parses(string value, AccelerationUnit expected)
    {
        Assert.Equal(expected, TurbulenceMonitor.ParseUnit(value));
    }

    private static void Park(TurbulenceDetector d, double raw, int samples, int startAt = 0)
    {
        for (var i = 0; i < samples; i++)
        {
            d.Process(new TurbulenceSample(T0.AddMilliseconds(100 * (startAt + i)), raw, true, 0, false));
        }
    }

    /// <summary>Level flight at <paramref name="raw"/> for <paramref name="seconds"/>; returns the next sample time.</summary>
    private static DateTimeOffset Fly(TurbulenceDetector d, double raw, double seconds, int from, DateTimeOffset? start = null)
    {
        var at = start ?? T0.AddMilliseconds(100 * from);
        var n = (int)Math.Round(seconds * 10);
        for (var i = 0; i < n; i++, at = at.AddMilliseconds(100))
        {
            d.Process(new TurbulenceSample(at, raw, false, 450, true));
        }

        return at;
    }
}
