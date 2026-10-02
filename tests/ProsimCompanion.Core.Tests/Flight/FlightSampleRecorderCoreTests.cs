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

    // ---- position (issue #145) ----

    private static FlightStateView WithPosition(GeoPoint? position)
        => new(FlightPhase.Cruise, new FlightDataSnapshot
        {
            IsValid = true,
            IsReady = true,
            IndicatedAirspeedKt = 280,
            Position = position,
        }, true, true);

    [Fact]
    public void Sample_WithAPosition_RoundTripsThroughJson_AtFourDecimals()
    {
        var sample = FlightSample.From(WithPosition(new GeoPoint(51.477512345, -0.461398765)));
        var json = System.Text.Json.JsonSerializer.Serialize(sample);

        Assert.Contains("\"lat\":51.4775", json, StringComparison.Ordinal);
        Assert.Contains("\"lon\":-0.4614", json, StringComparison.Ordinal);

        var back = System.Text.Json.JsonSerializer.Deserialize<FlightSample>(json)!;
        Assert.Equal(sample, back);
        Assert.Equal(new GeoPoint(51.4775, -0.4614), back.ToSnapshot().Position);
    }

    [Fact]
    public void Sample_WithoutAPosition_OmitsBothKeys_AndRoundTrips()
    {
        var sample = FlightSample.From(WithPosition(null));
        var json = System.Text.Json.JsonSerializer.Serialize(sample);

        Assert.DoesNotContain("\"lat\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"lon\"", json, StringComparison.Ordinal);

        var back = System.Text.Json.JsonSerializer.Deserialize<FlightSample>(json)!;
        Assert.Equal(sample, back);
        Assert.Null(back.ToSnapshot().Position);
    }

    [Fact]
    public void ARecordingMadeBeforePositions_StillParses_WithNoPosition()
    {
        // A flight-sample payload exactly as beta.28 wrote it (2026-09-13 EGLL→LIRF).
        const string old = """
            {"ph":"Cruise","og":false,"ias":276.4,"gs":452.1,"alt":36000,"ra":2500,"agl":35900,"vs":0,
             "pwr":true,"eng":true,"engRaw":true,"st":false,"pb":false,"pbRaw":3,"brk":false,"gear":false,
             "apu":false,"bcn":true,"thr":false,"n1":84,"n1avg":84,"flap":0,"fcu":36000,"fms":36000,
             "v1":141,"vr":144,"v2":147}
            """;

        var sample = System.Text.Json.JsonSerializer.Deserialize<FlightSample>(old)!;

        Assert.Null(sample.LatitudeDeg);
        Assert.Null(sample.LongitudeDeg);
        Assert.Null(sample.ToSnapshot().Position);
        Assert.Equal(452.1, sample.ToSnapshot().GroundSpeedKt);
    }

    [Fact]
    public void AParkedAircraft_WithPositionNoise_StaysSuppressed()
    {
        // Sub-metre jitter must not defeat the unchanged-sample suppression: a parked hour
        // would otherwise write 3,600 lines instead of a few hundred bytes.
        var core = new FlightSampleRecorderCore();
        FlightStateView Parked(double lon) => new(FlightPhase.Preflight, new FlightDataSnapshot
        {
            IsValid = true,
            IsReady = true,
            OnGround = true,
            Position = new GeoPoint(51.4775, lon),
        }, false, true);

        Assert.NotNull(core.Next(Parked(-0.461400), T0, FlightStateOptions.Default));
        Assert.Null(core.Next(Parked(-0.461401), T0.AddSeconds(1), FlightStateOptions.Default));
    }
}
