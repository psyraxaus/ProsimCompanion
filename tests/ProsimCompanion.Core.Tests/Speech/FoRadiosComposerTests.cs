using ProsimCompanion.Core.Flight;
using ProsimCompanion.Speech.SayIntentions;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>The FO works the radios (owner request 2026-10-25). Every expected line here is the
/// SayIntentions copilot's own, from the owner's 2026-10-25 EKCH→EFHK transcript — plus the
/// callsign closing each transmission (owner request 2026-10-29) and the 2026-10-29 EGLL
/// corrections (winds calm, thousands separators, broadcasts).</summary>
public sealed class FoRadiosComposerTests
{
    private const string Callsign = "Finnair-niner-six-two";

    [Theory]
    [InlineData("Finnair-niner-six-two, Taxi to holding point runway 22R via Yankee, Alpha, hold-short-of runway 12.", "Kastrup Ground",
        "Taxi to holding point runway 22R via Yankee, Alpha, hold short of runway 12, Finnair-niner-six-two.")]
    [InlineData("Finnair-niner-six-two, cross-runway one-two, then continue taxi via A1", "Kastrup Ground",
        "Cross runway one-two, then continue taxi via Alpha-one, Finnair-niner-six-two.")]
    [InlineData("Finnair-niner-six-two, Kastrup Tower, Runway 22R, winds 160 at 28 gusting 40, cleared for takeoff.", "Kastrup Tower",
        "Runway 22R, cleared for takeoff, Finnair-niner-six-two.")]
    [InlineData("Finnair-niner-six-two, Kastrup Departure, Identified. climb FL070", "Kastrup Departure",
        "Climb FL70, Finnair-niner-six-two.")]
    [InlineData("Finnair-niner-six-two, climb Flight-level-140", "Kastrup Departure",
        "Climb FL140, Finnair-niner-six-two.")]
    [InlineData("Finnair-niner-six-two, Copenhagen Control, Identified. Hej, climb FL290", "Copenhagen Control",
        "Climb FL290, Finnair-niner-six-two.")]
    [InlineData("Finnair-niner-six-two, Push and start approved. Face North-East.", "Kastrup Ground",
        "Push and start approved. Face North-East, Finnair-niner-six-two.")]
    // EGLL 2026-10-29 (Speedbird 850): "Winds calm" is winds too, and 6,000 keeps its comma.
    [InlineData("Finnair-niner-six-two, Runway 27R, Winds calm, cleared for takeoff.", "Heathrow Tower",
        "Runway 27R, cleared for takeoff, Finnair-niner-six-two.")]
    [InlineData("Finnair-niner-six-two, Heathrow Radar, climb altitude 6,000", "Heathrow Radar",
        "Climb altitude 6,000, Finnair-niner-six-two.")]
    [InlineData("Finnair-niner-six-two, London Control, Good morning, climb FL330", "London Control",
        "Climb FL330, Finnair-niner-six-two.")]
    public void Readback_MatchesTheCopilotsOwnLine_AndEndsWithTheCallsign(string atc, string station, string expected)
    {
        var readback = ReadbackComposer.Compose(atc, Callsign, station);

        Assert.NotNull(readback);
        Assert.Equal(ReadbackKind.Readback, readback.Kind);
        Assert.Equal(expected, readback.Transmit);
    }

    [Fact]
    public void Readback_WithoutACallsign_IsTheBareInstruction()
        => Assert.Equal("Climb FL140.", ReadbackComposer.Compose("climb Flight-level-140", null, null)!.Transmit);

    [Theory]
    [InlineData("Finnair-niner-six-two, Contact Tower on 119.355 . Have a Good evening", "Tower", 119_355, "Contacting Tower on 119.355, Finnair-niner-six-two.")]
    [InlineData("Finnair-niner-six-two, Contact Kastrup Departure on 120.255", "Kastrup Departure", 120_255, "Contacting Kastrup Departure on 120.255, Finnair-niner-six-two.")]
    [InlineData("Finnair-niner-six-two, Contact Copenhagen Control on 119.555.", "Copenhagen Control", 119_555, "Contacting Copenhagen Control on 119.555, Finnair-niner-six-two.")]
    public void Handoff_IsContacting_WithStationAndKhz(string atc, string station, int khz, string expected)
    {
        var readback = ReadbackComposer.Compose(atc, Callsign, "Kastrup Ground");

        Assert.NotNull(readback);
        Assert.Equal(ReadbackKind.Handoff, readback.Kind);
        Assert.Equal(expected, readback.Transmit);
        Assert.Equal(station, readback.HandoffStation);
        Assert.Equal(khz, readback.HandoffKhz);
    }

    [Theory]
    [InlineData("Finnair-niner-six-two, Identified. Good evening")]
    [InlineData("Finnair-niner-six-two, Sweden Control, Identified. God kvall")]
    [InlineData("Finnair-niner-six-two, Kastrup Departure, Identified.")]
    [InlineData("Finnair-niner-six-two, Heathrow Tower, Roger")]
    // Broadcasts (EGLL 2026-10-29: both were read back as "QNH 1028").
    [InlineData("All aircraft be advised, information Sierra is now current. QNH 1028.")]
    [InlineData("Attention all aircraft, Uniform now current QNH 1028.")]
    [InlineData("All stations, Heathrow Tower, runway change to 27L in ten minutes.")]
    [InlineData("")]
    public void CheckInRepliesAndBroadcasts_NeedNoReadback(string atc)
        => Assert.Null(ReadbackComposer.Compose(atc, Callsign, "Sweden Control"));

    [Theory]
    [InlineData("311", "three-one-one")]
    [InlineData("Stand 546R", "Stand 546R")] // the label is the template's job (AtcRequestTemplate strips it first)
    [InlineData("546R", "five-four-six-Romeo")]
    [InlineData("B16", "Bravo-one-six")]
    [InlineData("35", "three-five")]
    [InlineData("PIER 2 GATE STAND 208", "PIER 2 GATE STAND 208")]
    [InlineData("", "")]
    public void Stand_IsSpelledDigitByDigit_WhenCompact(string stand, string expected)
        => Assert.Equal(expected, RadioSpeech.Stand(stand));

    [Fact]
    public void Spoken_SpellsLevelsFrequenciesAndRunways()
    {
        Assert.Equal("Climb flight level one four zero.", RadioSpeech.Spoken("Climb FL140."));
        Assert.Equal("Contacting Tower on one one niner decimal three five five.", RadioSpeech.Spoken("Contacting Tower on 119.355."));
        Assert.Equal("Runway two two right, cleared for takeoff.", RadioSpeech.Spoken("Runway 22R, cleared for takeoff."));
        Assert.Equal("Squawk seven three five six.", RadioSpeech.Spoken("Squawk 7356."));
        Assert.Equal("Good evening, Finnair niner six two, maintaining flight level three seven zero.",
            RadioSpeech.Spoken("Good evening, Finnair-niner-six-two, maintaining FL370."));
    }

    [Theory]
    [InlineData("Finnair-niner-six-two, Kastrup Departure, Identified. climb FL070", "FL70")]
    [InlineData("Finnair-niner-six-two, climb Flight-level-140", "FL140")]
    [InlineData("Finnair-niner-six-two, descend to 5,000 feet, QNH 1013", "5,000 feet")]
    [InlineData("Cleared to Helsinki Vantaa via the KEMA5C departure. Initial climb to FL070. Departure on 120.255. Squawk 7356.", "FL70")]
    [InlineData("Finnair-niner-six-two, Contact Tower on 119.355", null)]
    public void ClearedLevel_IsReadFromTheInstruction(string atc, string? expected)
        => Assert.Equal(expected, ClearedLevelParser.Parse(atc));

    private static CheckInContext Context(FlightPhase phase, bool onGround, double altitude, string? cleared)
        => new("Good evening", Callsign, phase, onGround, altitude, cleared, "KEMA5C", "LAKU8M", "22R", "15", "B16");

    [Fact]
    public void CheckIn_Climb_MatchesTheCopilot()
    {
        Assert.Equal("Good evening, Finnair-niner-six-two, climbing through 2,500 on KEMA5C.",
            CheckInComposer.Compose(Context(FlightPhase.InitialClimb, false, 2_480, null))!.Transmit);
        Assert.Equal("Good evening, Finnair-niner-six-two, climbing through FL180 for FL210 on KEMA5C.",
            CheckInComposer.Compose(Context(FlightPhase.Climb, false, 18_040, "FL210"))!.Transmit);
        Assert.Equal("Good evening, Finnair-niner-six-two, climbing through FL250 for FL370 on KEMA5C.",
            CheckInComposer.Compose(Context(FlightPhase.Climb, false, 25_200, "FL370"))!.Transmit);
    }

    [Fact]
    public void CheckIn_Cruise_MaintainsTheLevel()
        => Assert.Equal("Good evening, Finnair-niner-six-two, maintaining FL370.",
            CheckInComposer.Compose(Context(FlightPhase.Cruise, false, 37_010, "FL370"))!.Transmit);

    [Fact]
    public void CheckIn_DescentAndApproach_UseTheStarAndRunway()
    {
        Assert.Equal("Good evening, Finnair-niner-six-two, descending through FL200 for FL120 on LAKU8M.",
            CheckInComposer.Compose(Context(FlightPhase.Descent, false, 20_100, "FL120"))!.Transmit);
        Assert.Equal("Good evening, Finnair-niner-six-two, descending through 4,000 for 3,000 feet on LAKU8M, inbound runway 15.",
            CheckInComposer.Compose(Context(FlightPhase.Approach, false, 4_030, "3,000 feet"))!.Transmit);
    }

    [Fact]
    public void CheckIn_Ground_TaxiOutAndTaxiIn()
    {
        Assert.Equal("Good evening, Finnair-niner-six-two, taxiing to holding point runway 22R.",
            CheckInComposer.Compose(Context(FlightPhase.TaxiOut, true, 20, null))!.Transmit);
        Assert.Equal("Good evening, Finnair-niner-six-two, runway 15 vacated, request taxi to stand Bravo-one-six.",
            CheckInComposer.Compose(Context(FlightPhase.TaxiIn, true, 180, null))!.Transmit);
        Assert.Equal("Good evening, Finnair niner six two, runway 15 vacated, request taxi to stand Bravo one six.",
            CheckInComposer.Compose(Context(FlightPhase.TaxiIn, true, 180, null))!.Spoken);
    }

    [Fact]
    public void CheckIn_NoGreeting_NoCallsign()
    {
        var noGreeting = CheckInComposer.Compose(Context(FlightPhase.Cruise, false, 37_000, null) with { Greeting = null });
        Assert.Equal("Finnair-niner-six-two, maintaining FL370.", noGreeting!.Transmit);
        Assert.Null(CheckInComposer.Compose(Context(FlightPhase.Cruise, false, 37_000, null) with { Callsign = "" }));
    }

    [Theory]
    [InlineData(2_480, "2,500")]
    [InlineData(9_960, "10,000")]
    [InlineData(10_000, "FL100")]
    [InlineData(18_040, "FL180")]
    [InlineData(37_010, "FL370")]
    public void LevelText_FeetBelowTenThousand_FlightLevelsAbove(double altitude, string expected)
        => Assert.Equal(expected, CheckInComposer.LevelText(altitude));
}
