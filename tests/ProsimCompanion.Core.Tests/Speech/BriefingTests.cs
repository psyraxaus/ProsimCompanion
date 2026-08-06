using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Briefings;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

public sealed class BriefingTests
{
    private static readonly NavDataFacts Nav = new(
        AiracCycle: "2508", TransitionAltitudeFt: 10_000, TransitionLevel: 110,
        RunwayTrueHeading: 163, RunwayLengthFt: 12_999, RunwayElevationFt: 21,
        IlsIdent: "IMEA", IlsFrequencyMhz: 110.3, GlideSlopeAngle: 3.0);

    private static BriefingFacts Departure() => new(
        IsDeparture: true, Airport: "YSSY", Runway: "16R", Sid: "FISHA1", Star: null,
        Approach: null, Nav: Nav, V1: 140, Vr: 145, V2: 150,
        WindDirDeg: 270, WindSpeedKt: 12, QnhHpa: 1013, Minima: null);

    [Fact]
    public void DepartureTemplate_ExactClauseStructure()
    {
        var text = BriefingComposer.Template(Departure());

        Assert.StartsWith("Departure briefing. Departing YSSY. Runway 16R. "
            + "Standard instrument departure FISHA1. Initial track 163 degrees. "
            + "Transition altitude 10000 feet. V1 140, rotate 145, V2 150. "
            + "Wind 270 at 12 knots. QNH 1013.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ArrivalTemplate_MinimaAlwaysLast()
    {
        var facts = Departure() with
        {
            IsDeparture = false,
            Approach = "ILS 16R",
            Minima = new ArrivalMinima(ArrivalMinimumKind.DecisionAltitude, 320),
        };

        var text = BriefingComposer.Template(facts);
        Assert.EndsWith("Minimums, decision altitude three two zero feet.", text, StringComparison.Ordinal);
        Assert.Contains("ILS IMEA, frequency 110.30.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifyNumbers_AcceptsFactNumbers_FlagsInventions()
    {
        var facts = Departure();
        var good = "Runway one six right, V1 140, transition altitude 10000 feet, QNH 1013.";
        Assert.Empty(BriefingComposer.VerifyNumbers(good, facts).Offending);

        var bad = "Transition altitude 18000 feet."; // invented — not in facts
        var offending = BriefingComposer.VerifyNumbers(bad, facts).Offending;
        Assert.Contains("18000", offending);
    }

    [Fact]
    public void VerifyNumbers_IgnoresShortTokens()
        // 1–2 digit tokens (runway numbers, flap settings) are deliberately not checked.
        => Assert.Empty(BriefingComposer.VerifyNumbers("Flaps 2, runway 16.", Departure()).Offending);

    [Theory]
    [InlineData("YSSY 070800Z 27012KT CAVOK 22/10 Q1013", 270, 12, 1013)]
    [InlineData("KLAX 070753Z 25008G18KT 10SM FEW015 18/12 A2992", 250, 8, 1013)]
    public void MetarBasics_WindAndQnh(string metar, int dir, int speed, int qnh)
    {
        var (windDir, windSpeed, qnhHpa) = BriefingService.ParseMetarBasics(metar);
        Assert.Equal(dir, windDir);
        Assert.Equal(speed, windSpeed);
        Assert.Equal(qnh, qnhHpa);
    }

    [Fact]
    public void FmsPlan_ParsesActiveRoute()
    {
        const string xml = """
            <fms><route type="act" origin="YSSY" destination="YMML"
                 originRunway="16R" destinationRunway="34"
                 flightplan="YSSY FISHA1.FISHA WOL H65 RAZZI RAZZI4.YMML" /></fms>
            """;

        var plan = BriefingService.ParseFmsPlan(xml);
        Assert.Equal("YSSY", plan.Origin);
        Assert.Equal("YMML", plan.Destination);
        Assert.Equal("16R", plan.OriginRunway);
        Assert.Equal("FISHA1", plan.Sid);
    }

    [Theory]
    [InlineData("16R", "RW16R")]
    [InlineData("6R", "RW06R")]
    [InlineData("RW34", "RW34")]
    [InlineData("", null)]
    public void RunwayNormalization(string input, string? expected)
        => Assert.Equal(expected, DfdNavDataProvider.NormalizeRunway(input));
}
