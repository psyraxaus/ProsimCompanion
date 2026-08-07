using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Speech.Cabin;
using ProsimCompanion.Speech.Company;
using ProsimCompanion.Speech.Playback;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

public sealed class CabinCrewCoreTests
{
    private static CabinTickSample Sample(
        FlightPhase phase,
        bool doorsClosed = true,
        bool beaconOn = true,
        int signs = 1,
        double altFt = 5000)
        => new(phase, doorsClosed, beaconOn, signs, altFt);

    private static readonly Func<double> NeverRoll = () => 1.0;

    [Fact]
    public void SecureReport_FiresOnceWithDoorsAndBeacon()
    {
        var core = new CabinCrewCore();
        var options = new CabinOptions();

        Assert.Equal(CabinAction.SecureReport,
            core.Evaluate(Sample(FlightPhase.PushbackAndStart), options, NeverRoll));
        Assert.Equal(CabinAction.None,
            core.Evaluate(Sample(FlightPhase.TaxiOut), options, NeverRoll));
    }

    [Theory]
    [InlineData(false, true)]   // a door open
    [InlineData(true, false)]   // beacon off
    public void SecureReport_RequiresDoorsAndBeacon(bool doorsClosed, bool beaconOn)
    {
        var core = new CabinCrewCore();

        Assert.Equal(CabinAction.None, core.Evaluate(
            Sample(FlightPhase.PushbackAndStart, doorsClosed, beaconOn),
            new CabinOptions(), NeverRoll));
    }

    [Fact]
    public void ReadyReport_FiresBelowAltitudeWithSignsOn()
    {
        var core = new CabinCrewCore();
        var options = new CabinOptions();

        Assert.Equal(CabinAction.None,
            core.Evaluate(Sample(FlightPhase.Descent, altFt: 12000), options, NeverRoll));
        Assert.Equal(CabinAction.ReadyReport,
            core.Evaluate(Sample(FlightPhase.Descent, altFt: 9000), options, NeverRoll));
        Assert.Equal(CabinAction.None,
            core.Evaluate(Sample(FlightPhase.Approach, altFt: 3000), options, NeverRoll));
    }

    [Fact]
    public void ReadyReport_SignsAutoDoesNotTrigger()
    {
        // S_OH_SIGNS is 3-state: 0=Auto 1=On 2=Off. Only ON counts (predecessor parity).
        var core = new CabinCrewCore();

        Assert.Equal(CabinAction.None, core.Evaluate(
            Sample(FlightPhase.Descent, signs: 0, altFt: 9000), new CabinOptions(), NeverRoll));
    }

    [Fact]
    public void ReadyReport_ZeroAltitudeMeansNoData()
    {
        var core = new CabinCrewCore();

        Assert.Equal(CabinAction.None, core.Evaluate(
            Sample(FlightPhase.Descent, altFt: 0), new CabinOptions(), NeverRoll));
    }

    [Fact]
    public void Rearm_OnColdAndDark_AndOnTurnaroundPreflight()
    {
        var core = new CabinCrewCore();
        var options = new CabinOptions();
        Assert.Equal(CabinAction.SecureReport,
            core.Evaluate(Sample(FlightPhase.PushbackAndStart), options, NeverRoll));

        // The predecessor only re-armed at ColdAndDark; a turnaround Preflight re-arms too.
        core.OnPhaseChanged(FlightPhase.Shutdown, FlightPhase.Preflight);
        Assert.Equal(CabinAction.SecureReport,
            core.Evaluate(Sample(FlightPhase.PushbackAndStart), options, NeverRoll));

        core.OnPhaseChanged(FlightPhase.Shutdown, FlightPhase.ColdAndDark);
        Assert.Equal(CabinAction.SecureReport,
            core.Evaluate(Sample(FlightPhase.PushbackAndStart), options, NeverRoll));

        // A mid-flight phase change must NOT re-arm.
        core.OnPhaseChanged(FlightPhase.Climb, FlightPhase.Cruise);
        Assert.Equal(CabinAction.None,
            core.Evaluate(Sample(FlightPhase.PushbackAndStart), options, NeverRoll));
    }

    [Fact]
    public void BoardingDelay_OneRollPerFlight_LosingRollConsumesIt()
    {
        var core = new CabinCrewCore();
        var options = new CabinOptions { AmbientEvents = true, BoardingDelayProbability = 0.5 };

        // Losing roll (1.0 ≥ 0.5) consumes the flight's one chance.
        Assert.Equal(CabinAction.None,
            core.Evaluate(Sample(FlightPhase.Preflight, beaconOn: false), options, NeverRoll));
        Assert.Equal(CabinAction.None,
            core.Evaluate(Sample(FlightPhase.Preflight, beaconOn: false), options, () => 0.0));

        core.OnPhaseChanged(FlightPhase.Shutdown, FlightPhase.ColdAndDark);
        Assert.Equal(CabinAction.BoardingDelay,
            core.Evaluate(Sample(FlightPhase.Preflight, beaconOn: false), options, () => 0.0));
    }

    [Fact]
    public void BoardingDelay_OffByDefault()
    {
        var core = new CabinCrewCore();

        Assert.Equal(CabinAction.None, core.Evaluate(
            Sample(FlightPhase.Preflight, beaconOn: false), new CabinOptions(), () => 0.0));
    }
}

public sealed class CompanyChannelTests
{
    [Fact]
    public void BuildLoadsheet_FormatsTonnesAndCg()
    {
        var text = CompanyChannelService.BuildLoadsheet(
            zfwKg: 62_340, towKg: 71_260, fobKg: 8_920, cgPercent: 28.34, pax: 174);

        Assert.Equal(
            "Loadsheet. Zero fuel weight 62.3 tonnes. Take-off weight 71.3 tonnes. " +
            "Fuel on board 8.9 tonnes. Passengers, 174. Center of gravity 28.3 percent.",
            text);
    }

    [Fact]
    public void BuildLoadsheet_InvariantUnderEuropeanCulture()
    {
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var text = CompanyChannelService.BuildLoadsheet(62_340, 0, 0, 0, 0);
            Assert.Contains("62.3", text, StringComparison.Ordinal);
            Assert.DoesNotContain("62,3", text, StringComparison.Ordinal);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }

    [Fact]
    public void BuildLoadsheet_OmitsUnpopulatedFigures()
        => Assert.Equal("Loadsheet.", CompanyChannelService.BuildLoadsheet(0, 0, 0, 0, 0));
}

public sealed class ChimeSynthTests
{
    [Theory]
    [InlineData("cabin")]
    [InlineData("company")]
    [InlineData("acars")]
    [InlineData("  Cabin ")]   // trimmed + case-insensitive
    public void KnownIds_ProduceValidWav(string id)
    {
        var wav = ChimeSynth.Build(id);

        Assert.NotNull(wav);
        Assert.True(wav!.Length > 44);
        Assert.Equal("RIFF"u8.ToArray(), wav[..4]);
        Assert.Equal("WAVE"u8.ToArray(), wav[8..12]);
        // RIFF size field must match the actual payload.
        Assert.Equal(wav.Length - 8, BitConverter.ToInt32(wav, 4));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("gong")]
    public void UnknownIds_AreNull(string? id)
        => Assert.Null(ChimeSynth.Build(id));

    [Fact]
    public void CabinAndCompany_AreDistinctChimes()
    {
        var cabin = ChimeSynth.Build("cabin")!;
        var company = ChimeSynth.Build("company")!;

        Assert.NotEqual(cabin.Length, company.Length); // 475 ms ding-dong vs 240 ms double-beep
    }
}
