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
        double altFt = 5000,
        double vsFpm = 0,
        bool airborne = true)
        => new(phase, doorsClosed, beaconOn, signs, altFt, vsFpm, airborne);

    private static readonly Func<double> NeverRoll = () => 1.0;

    private static readonly DateTimeOffset T0 = new(2026, 9, 27, 10, 0, 0, TimeSpan.Zero);

    /// <summary>The pre-#134 behaviour: no securing wait at all.</summary>
    private static CabinOptions Instant() => new() { CabinSecureMinDelaySeconds = 0, CabinSecureSecondsPerPax = 0 };

    [Fact]
    public void SecureReport_FiresOnceWithDoorsAndBeacon()
    {
        var core = new CabinCrewCore();
        var options = Instant();

        Assert.Equal(CabinAction.SecureReport,
            core.Evaluate(Sample(FlightPhase.PushbackAndStart), options, NeverRoll));
        Assert.Equal(CabinAction.None,
            core.Evaluate(Sample(FlightPhase.TaxiOut), options, NeverRoll));
    }

    [Fact]
    public void SecureReport_WaitsForTheDrawnDelay()
    {
        // Issue #134 (Nico): doors closed + beacon on arms min + roll × pax × perPax; the
        // report fires only when that expires. 30 s + 0.5 × 100 pax × 1 s/pax = 80 s.
        var core = new CabinCrewCore();
        var options = new CabinOptions { CabinSecureMinDelaySeconds = 30, CabinSecureSecondsPerPax = 1.0 };
        CabinTickSample At(int seconds) => Sample(FlightPhase.PushbackAndStart) with
        {
            PaxOnBoard = 100,
            NowUtc = T0.AddSeconds(seconds),
        };

        Assert.Equal(CabinAction.SecureArmed, core.Evaluate(At(0), options, () => 0.5));
        Assert.Equal(80, core.SecureDelaySeconds);
        Assert.Equal(T0.AddSeconds(80), core.SecureDueAtUtc);

        Assert.Equal(CabinAction.None, core.Evaluate(At(1), options, NeverRoll));
        Assert.Equal(CabinAction.None, core.Evaluate(At(79), options, NeverRoll));
        Assert.Equal(CabinAction.SecureReport, core.Evaluate(At(80) with { Phase = FlightPhase.TaxiOut }, options, NeverRoll));
        Assert.Null(core.SecureDueAtUtc);
        Assert.Equal(CabinAction.None, core.Evaluate(At(81), options, NeverRoll));
    }

    [Fact]
    public void SecureTimer_HoldsTheReportWhileADoorIsOpen_NeverCancels()
    {
        // A re-opened door (or beacon off during a paused push) after arming holds the
        // report until the conditions are back; the drawn wait is not re-rolled.
        var core = new CabinCrewCore();
        var options = new CabinOptions { CabinSecureMinDelaySeconds = 10, CabinSecureSecondsPerPax = 0 };

        Assert.Equal(CabinAction.SecureArmed,
            core.Evaluate(Sample(FlightPhase.PushbackAndStart) with { NowUtc = T0 }, options, NeverRoll));
        Assert.Equal(CabinAction.None,
            core.Evaluate(Sample(FlightPhase.PushbackAndStart, doorsClosed: false) with { NowUtc = T0.AddSeconds(20) }, options, NeverRoll));
        Assert.Equal(10, core.SecureDelaySeconds);
        Assert.Equal(CabinAction.SecureReport,
            core.Evaluate(Sample(FlightPhase.PushbackAndStart) with { NowUtc = T0.AddSeconds(21) }, options, NeverRoll));
    }

    [Theory]
    [InlineData(45, 1.0, 180, 0.0, 45)]     // losing roll: minimum only
    [InlineData(45, 1.0, 180, 1.0, 225)]    // full spread
    [InlineData(45, 1.0, 0, 1.0, 45)]       // no pax known: minimum only
    [InlineData(0, 0.0, 180, 1.0, 0)]       // instant (pre-#134 behaviour)
    [InlineData(-5, -1.0, 180, 1.0, 0)]     // misconfigured negatives degrade to instant
    [InlineData(60, 0.5, 150, 0.4, 90)]     // 60 + 0.4 × 150 × 0.5 = 90
    public void SecureDelay_IsMinimumPlusRandomShareOfPaxFactor(int min, double perPax, int pax, double roll, int expected)
    {
        var options = new CabinOptions { CabinSecureMinDelaySeconds = min, CabinSecureSecondsPerPax = perPax };

        Assert.Equal(expected, CabinCrewCore.SecureDelay(options, pax, roll));
    }

    [Fact]
    public void Rearm_ClearsTheSecureTimer()
    {
        var core = new CabinCrewCore();
        var options = new CabinOptions { CabinSecureMinDelaySeconds = 10, CabinSecureSecondsPerPax = 0 };

        Assert.Equal(CabinAction.SecureArmed,
            core.Evaluate(Sample(FlightPhase.PushbackAndStart) with { NowUtc = T0 }, options, NeverRoll));
        core.OnPhaseChanged(FlightPhase.Shutdown, FlightPhase.ColdAndDark);
        Assert.Null(core.SecureDueAtUtc);
        Assert.Equal(0, core.SecureDelaySeconds);

        // The next leg draws afresh — it does not inherit the expired timer.
        Assert.Equal(CabinAction.SecureArmed,
            core.Evaluate(Sample(FlightPhase.PushbackAndStart) with { NowUtc = T0.AddMinutes(30) }, options, NeverRoll));
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
    public void ReadyReport_NeverFiresWhileClimbing()
    {
        // Issue #48: a spurious Approach phase during climb-out (stuck gear lever) made the
        // cabin announce "secure for landing" on takeoff — the VS guard blocks it.
        var core = new CabinCrewCore();
        var options = new CabinOptions();

        Assert.Equal(CabinAction.None,
            core.Evaluate(Sample(FlightPhase.Approach, altFt: 3000, vsFpm: 2000), options, NeverRoll));

        // Once genuinely descending, the report still fires (once).
        Assert.Equal(CabinAction.ReadyReport,
            core.Evaluate(Sample(FlightPhase.Approach, altFt: 3000, vsFpm: -800), options, NeverRoll));
    }

    [Fact]
    public void ReadyReport_RequiresHavingBeenAirborneThisSession()
    {
        // Issue #59 (flight test 2026-08-16): a bogus startup Approach classification fired
        // "secure for landing" on a cold aircraft at the gate. Without an actual flight this
        // session, the landing report must never fire.
        var core = new CabinCrewCore();
        var options = new CabinOptions();

        Assert.Equal(CabinAction.None,
            core.Evaluate(Sample(FlightPhase.Approach, altFt: 3000, airborne: false), options, NeverRoll));

        // Once the session has genuinely been airborne, the same conditions fire the report.
        Assert.Equal(CabinAction.ReadyReport,
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
        var options = Instant();
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
