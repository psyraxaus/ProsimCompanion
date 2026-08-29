using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Flight;
using Xunit;

namespace ProsimCompanion.Core.Tests.Flight;

public sealed class FlightSampleRecorderCoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 29, 9, 0, 0, TimeSpan.Zero);

    private static FlightStateView Live(FlightPhase phase = FlightPhase.Preflight, double ias = 0, bool live = true)
        => new(phase, new FlightDataSnapshot { IsValid = true, IsReady = true, OnGround = true, IndicatedAirspeedKt = ias },
            HasBeenAirborneThisSession: false, IsLive: live);

    [Fact]
    public void NotLive_RecordsNothing()
    {
        var core = new FlightSampleRecorderCore();

        Assert.Null(core.Next(Live(live: false), T0, FlightStateOptions.Default));
        Assert.Null(core.Next(new FlightStateView(FlightPhase.Unknown, null, false, true), T0, FlightStateOptions.Default));
    }

    [Fact]
    public void Disabled_RecordsNothing()
    {
        var core = new FlightSampleRecorderCore();

        Assert.Null(core.Next(Live(), T0, new FlightStateOptions { RecordSamples = false }));
    }

    [Fact]
    public void UnchangedSamples_AreSuppressed_UntilTheKeepAlive()
    {
        var core = new FlightSampleRecorderCore();
        var options = new FlightStateOptions { SampleKeepAliveSeconds = 10 };

        Assert.NotNull(core.Next(Live(), T0, options));
        Assert.Null(core.Next(Live(), T0.AddSeconds(1), options));
        Assert.Null(core.Next(Live(), T0.AddSeconds(9), options));
        Assert.NotNull(core.Next(Live(), T0.AddSeconds(10), options));
    }

    [Fact]
    public void AChangedSample_IsRecordedAtOnce()
    {
        var core = new FlightSampleRecorderCore();

        Assert.NotNull(core.Next(Live(), T0, FlightStateOptions.Default));
        var changed = core.Next(Live(ias: 12), T0.AddSeconds(1), FlightStateOptions.Default);

        Assert.NotNull(changed);
        Assert.Equal(12, changed.IasKt);
        Assert.Equal("Preflight", changed.Phase);
    }

    [Fact]
    public void GoingNotLive_ForgetsTheLastSample_SoTheNextLiveOneIsWritten()
    {
        var core = new FlightSampleRecorderCore();

        Assert.NotNull(core.Next(Live(), T0, FlightStateOptions.Default));
        Assert.Null(core.Next(Live(live: false), T0.AddSeconds(1), FlightStateOptions.Default));
        Assert.NotNull(core.Next(Live(), T0.AddSeconds(2), FlightStateOptions.Default));
    }

    [Fact]
    public void Sample_RoundTripsToTheSnapshotTheRulesRead()
    {
        var view = new FlightStateView(FlightPhase.Approach, new FlightDataSnapshot
        {
            IsValid = true,
            IsReady = true,
            OnGround = false,
            IndicatedAirspeedKt = 143.26,
            RadioAltitudeFt = 1499.6,
            VerticalSpeedFpm = -712.4,
            GearDown = true,
            FmsCruiseAltFt = 36000,
            BeaconOn = true,
        }, false, true);

        var back = FlightSample.From(view).ToSnapshot();

        Assert.True(back.IsValid);
        Assert.True(back.IsReady);
        Assert.Equal(143.3, back.IndicatedAirspeedKt);
        Assert.Equal(1500, back.RadioAltitudeFt);
        Assert.Equal(-712, back.VerticalSpeedFpm);
        Assert.True(back.GearDown);
        Assert.True(back.BeaconOn);
        Assert.Equal(36000, back.FmsCruiseAltFt);
        Assert.Equal(FlightPhase.Approach, FlightPhaseEvaluator.Evaluate(back, FlightPhase.Descent));
    }
}
