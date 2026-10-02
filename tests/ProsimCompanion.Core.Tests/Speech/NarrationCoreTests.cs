using ProsimCompanion.Core.Debrief;
using ProsimCompanion.Core.Speech;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Briefings;
using ProsimCompanion.Speech.Llm;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>Issue #147: per-sentence verification and the template takeover — which
/// sections the pilot has already heard, in whatever words the model used.</summary>
public sealed class NarrationCoreTests
{
    private static readonly NavDataFacts Nav = new(
        AiracCycle: "2508", TransitionAltitudeFt: 5000, TransitionLevel: 110,
        RunwayTrueHeading: 163, RunwayLengthFt: 12_999, RunwayElevationFt: 21,
        IlsIdent: "IMEA", IlsFrequencyMhz: 110.30, GlideSlopeAngle: 3.0);

    private static readonly BriefingFacts Departure = new(
        IsDeparture: true, Airport: "YSSY", Runway: "16R", Sid: "FISHA1", Star: null, Approach: null,
        Nav, V1: 141, Vr: 144, V2: 147, WindDirDeg: 250, WindSpeedKt: 14, QnhHpa: 1013, Minima: null,
        AirportName: "Sydney");

    private static readonly BriefingFacts Arrival = new(
        IsDeparture: false, Airport: "YMML", Runway: "34", Sid: null, Star: "ARBEY1", Approach: "I34",
        Nav, V1: null, Vr: null, V2: null, WindDirDeg: 350, WindSpeedKt: 8, QnhHpa: 1021,
        Minima: new ArrivalMinima(ArrivalMinimumKind.DecisionAltitude, 640),
        AirportName: "Melbourne");

    private static NarrationCore Core(BriefingFacts facts)
        => new(BriefingComposer.AllowedNumbers(facts), BriefingComposer.Sections(facts));

    private static string Keys(IEnumerable<NarrationSection> sections) => string.Join(",", sections.Select(s => s.Key));

    // ---- the sections ARE the template ----

    [Fact]
    public void BriefingSections_JoinedAreTheTemplate_InItsOrder()
    {
        Assert.Equal(
            "Departure briefing. Departing Sydney. Runway 16R. Standard instrument departure FISHA one. "
            + "Initial track 163 degrees. Transition altitude 5000 feet. V1 141, rotate 144, V2 147. "
            + "Wind 250 at 14 knots. QNH 1013.",
            BriefingComposer.Template(Departure));
        Assert.Equal(
            "header,airport,runway,sid,track,transition-altitude,v-speeds,wind,qnh",
            Keys(BriefingComposer.Sections(Departure)));
        Assert.Equal(
            "header,airport,runway,approach,ils,glideslope,star,transition-level,wind,qnh,minimums",
            Keys(BriefingComposer.Sections(Arrival)));
        Assert.Equal(
            string.Join(" ", BriefingComposer.Sections(Arrival).Select(s => s.Text)),
            BriefingComposer.Template(Arrival));
    }

    [Fact]
    public void DebriefSections_JoinedAreTheTemplate()
    {
        var facts = DebriefFacts.Empty with
        {
            BlockMinutes = 130, FlightMinutes = 112, LiftoffIasKt = 151.6, TouchdownGroundSpeedKt = 131.2,
            ChecklistsCompleted = 4, DefectsRaised = 1, CalloutsFired = 12, FinalFobKg = 6700,
        };

        var sections = DebriefTemplate.Sections(facts, DebriefVerbosity.Full);

        Assert.Equal(DebriefTemplate.Build(facts, DebriefVerbosity.Full), string.Join(" ", sections.Select(s => s.Text)));
        Assert.Equal("header,block,airborne,liftoff,touchdown,checklists,defects-raised,callouts,fuel,sign-off", Keys(sections));
        Assert.True(sections[0].IsOpening);
        Assert.True(sections[^1].IsClosing);
    }

    // ---- verification per sentence ----

    [Fact]
    public void Accept_ReleasesASentenceWhoseNumbersAreFacts_DigitsOrWords()
    {
        var core = Core(Departure);

        Assert.True(core.Accept("Good morning Captain, this is the departure briefing for Sydney."));
        Assert.True(core.Accept("We depart runway one six right on the FISHA one departure."));
        Assert.True(core.Accept("Initial track one six three degrees, transition altitude 5000 feet."));
        Assert.True(core.Accept("V1 141, rotate 144, V2 147."));

        Assert.Equal(4, core.Spoken.Count);
    }

    [Fact]
    public void Accept_RejectsAnInventedNumber_SpelledOrNot_AndDoesNotCountItAsSaid()
    {
        var core = Core(Departure);

        Assert.False(core.Accept("Initial track one six eight degrees."));
        Assert.Equal("168", Assert.Single(core.LastOffending));
        Assert.False(core.Accept("Transition altitude 6000 feet."));
        Assert.False(core.Accept("Climb to seven thousand feet."));
        Assert.Empty(core.Spoken);
    }

    // ---- takeover: what is left to say ----

    [Fact]
    public void Remaining_WithNothingSaid_IsTheWholeTemplate()
        => Assert.Equal(
            "header,airport,runway,sid,track,transition-altitude,v-speeds,wind,qnh",
            Keys(Core(Departure).Remaining()));

    [Fact]
    public void Remaining_SkipsWhatWasHeard_InTheModelsOwnWords_AndKeepsTemplateOrder()
    {
        var core = Core(Departure);
        core.Accept("Good morning Captain, departure briefing for Sydney.");
        core.Accept("We are departing runway one six right via the FISHA one departure.");
        // The model jumped ahead to the weather before the takeover.
        core.Accept("The wind is two five zero at one four knots.");

        // The header, airport, runway, SID and wind are behind us; the rest follows in order.
        Assert.Equal("track,transition-altitude,v-speeds,qnh", Keys(core.Remaining()));
    }

    [Fact]
    public void Remaining_AFactOnlyHalfSaid_IsSaidAgain_NeverLost()
    {
        var core = Core(Departure);
        // "Runway" without the designator, one V-speed of three, the QNH word without the value.
        core.Accept("We will use the usual runway today.");
        core.Accept("V1 is 141.");
        core.Accept("I will set the QNH later.");

        var remaining = Keys(core.Remaining());

        Assert.Contains("runway", remaining, StringComparison.Ordinal);
        Assert.Contains("v-speeds", remaining, StringComparison.Ordinal);
        Assert.Contains("qnh", remaining, StringComparison.Ordinal);
        Assert.DoesNotContain("header", remaining, StringComparison.Ordinal);
    }

    [Fact]
    public void Remaining_Arrival_RecognisesApproachIlsStarAndMinimums()
    {
        var core = Core(Arrival);
        core.Accept("Arrival briefing for Melbourne, landing runway three four.");
        core.Accept("It is the I L S approach, frequency one one zero decimal three zero, glideslope three point zero degrees.");
        core.Accept("Minimums are decision altitude six four zero feet.");

        Assert.Equal("star,transition-level,wind,qnh", Keys(core.Remaining()));
    }

    [Theory]
    [InlineData("16R", "departing runway one six right", true)]
    [InlineData("16R", "runway 16R is in use", true)]
    [InlineData("16R", "departing runway one six left", false)]
    [InlineData("16R", "one six knots on the right", false)]
    [InlineData("04L", "runway zero four left", true)]
    [InlineData("34", "landing runway three four", true)]
    [InlineData("34", "runway three four left", false)]       // a different runway
    [InlineData("34", "wind three four zero at three four knots", false)]
    public void Runway_IsRecognisedOnlyWithItsOwnDesignator(string designator, string spoken, bool covered)
        => Assert.Equal(covered, NarrationCore.IsCovered(new NarrationSection("runway", "x") { Runway = designator }, spoken));

    [Fact]
    public void ASectionWithNothingToRecogniseItBy_IsAlwaysSaid_AndTheClosingLineToo()
    {
        Assert.False(NarrationCore.IsCovered(new NarrationSection("free", "Have a nice day."), "Have a nice day."));
        Assert.False(NarrationCore.IsCovered(new NarrationSection("bye", "Good flight.") { IsClosing = true }, "Good flight."));
        Assert.True(NarrationCore.IsCovered(new NarrationSection("hello", "Debrief.") { IsOpening = true }, "Anything."));
        Assert.False(NarrationCore.IsCovered(new NarrationSection("hello", "Debrief.") { IsOpening = true }, "  "));
    }

    [Fact]
    public void Debrief_Takeover_FinishesWithTheUnsaidLines_AndAlwaysTheSignOff()
    {
        var facts = DebriefFacts.Empty with
        {
            BlockMinutes = 130, FlightMinutes = 112, TouchdownGroundSpeedKt = 131,
            ChecklistsCompleted = 4, CalloutsFired = 12, FinalFobKg = 6700,
        };
        var core = new NarrationCore(DebriefLlm.AllowedNumbers(facts), DebriefTemplate.Sections(facts, DebriefVerbosity.Full));

        Assert.True(core.Accept("Nice work today, Captain."));
        Assert.True(core.Accept("Block time was 130 minutes with 112 minutes airborne."));
        Assert.True(core.Accept("We completed four checklists."));
        Assert.False(core.Accept("Touchdown was at 155 knots."));      // invented: takeover

        Assert.Equal("touchdown,callouts,fuel,sign-off", Keys(core.Remaining()));
    }
}
