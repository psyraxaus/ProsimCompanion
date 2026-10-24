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

        // The SID renders phonetically (issue #68) — "FISHA1" spoken is "FISHA one".
        Assert.StartsWith("Departure briefing. Departing YSSY. Runway 16R. "
            + "Standard instrument departure FISHA one. Initial track 163 degrees. "
            + "Transition altitude 10000 feet. V1 140, rotate 145, V2 150. "
            + "Wind 270 at 12 knots. QNH 1013.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Template_SpeaksAirportName_WhenResolved()
    {
        // Issue #70: the spoken name replaces the ICAO in the template; the ICAO stays in
        // the facts for the route breadcrumb/verifier.
        var text = BriefingComposer.Template(Departure() with { AirportName = "Sydney" });
        Assert.Contains("Departing Sydney.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("YSSY", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FactBlock_PhoneticIdentifiers_AndNamedAirport()
    {
        var block = BriefingComposer.FactBlock(Departure() with
        {
            AirportName = "Sydney",
            AtisLetter = "M",
        });

        Assert.Contains("- Airport: Sydney (YSSY)", block, StringComparison.Ordinal);
        Assert.Contains("- SID: FISHA one", block, StringComparison.Ordinal);
        Assert.Contains("- ATIS information: Mike", block, StringComparison.Ordinal); // issue #68
    }

    [Fact]
    public void SpokenApproach_DfdIdentifierDecodes_ManualEntryRendersPhonetically()
    {
        // DFD identifier + matching runway → the same spoken form as "which approach".
        Assert.Equal("ILS Yankee", BriefingComposer.SpokenApproach("I16RY", "16R"));
        // Manual entry (or no runway) → phonetic identifier rendering.
        Assert.Equal("ILS one six Romeo", BriefingComposer.SpokenApproach("ILS 16R", "16R"));
        Assert.Equal("India one six Romeo Yankee", BriefingComposer.SpokenApproach("I16RY", null));
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
    public void FmsPlan_ParsesElementFormRoute()
    {
        // Real ProSim payloads carry the route fields as child ELEMENTS, and the LNAV string
        // uses IDENT.TRANSITION notation with the destination as the final token.
        const string xml = """
            <fms><route type="act">
                <origin>YSSY</origin><destination>YMML</destination>
                <originRunway>16R</originRunway><destinationRunway>34</destinationRunway>
                <flightplan>YSSY FISHA1.FISHA WOL H65 RAZZI RAZZI4.RAZZI YMML</flightplan>
                <legs>
                    <leg><type>EndOfFlightPlan</type></leg>
                    <leg><type>Destination</type><displayString>YSCB</displayString></leg>
                </legs>
            </route></fms>
            """;

        var plan = FmsPlanParser.Parse(xml);
        Assert.Equal("YSSY", plan.Origin);
        Assert.Equal("YMML", plan.Destination);
        Assert.Equal("16R", plan.OriginRunway);
        Assert.Equal("34", plan.DestinationRunway);
        Assert.Equal("FISHA1", plan.Sid);
        // The identifier is the part BEFORE the dot — the part after is the transition,
        // which no DFD procedure lookup would ever match.
        Assert.Equal("RAZZI4", plan.Star);
        Assert.Equal("YSCB", plan.Alternate);
    }

    [Fact]
    public void FmsPlan_AttributeFormStillParses()
    {
        const string xml = """
            <fms><route type="act" origin="YSSY" destination="YMML"
                 originRunway="16R" destinationRunway="34"
                 flightplan="YSSY FISHA1.FISHA WOL H65 RAZZI RAZZI4.RAZZI YMML" /></fms>
            """;

        var plan = BriefingService.ParseFmsPlan(xml);
        Assert.Equal("YSSY", plan.Origin);
        Assert.Equal("16R", plan.OriginRunway);
        Assert.Equal("FISHA1", plan.Sid);
        Assert.Equal("RAZZI4", plan.Star);
    }

    [Fact]
    public void FmsPlan_NonDottedTokens_YieldNoProcedures()
        // Airways / DCT / plain waypoints have no dot — no false positives.
        => Assert.Equal((null, null), FmsPlanParser.ParseSidStar("YSSY WOL H65 RAZZI YMML"));

    [Theory]
    [InlineData("16R", "RW16R")]
    [InlineData("6R", "RW06R")]
    [InlineData("RW34", "RW34")]
    [InlineData("", null)]
    public void RunwayNormalization(string input, string? expected)
        => Assert.Equal(expected, DfdNavDataProvider.NormalizeRunway(input));

    [Fact]
    public void ApproachIdentifiers_DecodeAndRankIlsFirst()
    {
        Assert.True(DfdNavDataProvider.TryDecodeApproachIdentifier("I04LY", "04L", out var ils, out var ilsRank));
        Assert.Equal("ILS", ils.Kind);
        Assert.Equal("ILS Yankee", ils.Spoken);

        Assert.True(DfdNavDataProvider.TryDecodeApproachIdentifier("R04LA", "04L", out var rnav, out var rnavRank));
        Assert.Equal("RNAV", rnav.Kind);
        Assert.True(ilsRank < rnavRank);

        // Exact side match: an 04R approach never serves 04L, and side-less runways only
        // match side-less identifiers.
        Assert.False(DfdNavDataProvider.TryDecodeApproachIdentifier("I04RY", "04L", out _, out _));
        Assert.False(DfdNavDataProvider.TryDecodeApproachIdentifier("I04LY", "04", out _, out _));
    }

    [Fact]
    public void MissedApproachLegs_DescribeCommonTypes()
    {
        Assert.Equal("heading zero four zero, climb to 600 feet",
            DfdNavDataProvider.DescribeLeg("CA", null, 40, 600, null));
        Assert.Equal("left turn, direct SOSIJ, climb to 3000 feet",
            DfdNavDataProvider.DescribeLeg("DF", "SOSIJ", null, 3000, 'L'));
        Assert.Equal("hold at TESAT", DfdNavDataProvider.DescribeLeg("HM", "TESAT", 90, null, null));
    }

    [Fact]
    public void RunwaySpoken_DigitsAndSideWord()
    {
        Assert.Equal("one six right", BriefingService.RunwaySpoken("16R"));
        Assert.Equal("zero four left", BriefingService.RunwaySpoken("RW04L"));
    }

    [Theory]
    [InlineData("decision altitude three two zero", ArrivalMinimumKind.DecisionAltitude, 320)]
    [InlineData("decision height 200 feet", ArrivalMinimumKind.DecisionHeight, 200)]
    [InlineData("minimum descent altitude 650", ArrivalMinimumKind.MinimumDescentAltitude, 650)]
    [InlineData("minimums 320", ArrivalMinimumKind.DecisionAltitude, 320)] // kind defaults to DA
    public void MinimaParser_KindAndValue(string utterance, ArrivalMinimumKind kind, double feet)
    {
        var minima = MinimaParser.TryParse(utterance);
        Assert.NotNull(minima);
        Assert.Equal(kind, minima.Kind);
        Assert.Equal(feet, minima.AltitudeFt);
    }

    [Fact]
    public void MinimaParser_RejectsGarbage_AndRecognizesNotBriefed()
    {
        Assert.Null(MinimaParser.TryParse("cabin crew seats for landing"));
        Assert.Null(MinimaParser.TryParse("minimums zero")); // 0 is not a plausible minimum
        Assert.True(MinimaParser.IsNotBriefed("minimums not briefed"));
        Assert.False(MinimaParser.IsNotBriefed("decision altitude 320"));
    }

    // ---- Tech-log consultation (2026-10-09) ----

    private static Core.TechLog.TechLogDefect Defect(string title, Core.TechLog.MelCategory category = Core.TechLog.MelCategory.C)
        => new() { Id = title, Title = title, Category = category, Status = Core.TechLog.DefectStatus.Deferred };

    [Fact]
    public void TechLogClause_ComesAfterEveryExistingClause_AndIsAbsentWhenTheLogIsClean()
    {
        var clean = BriefingComposer.Template(Departure());
        var withItems = BriefingComposer.Template(Departure() with
        {
            TechLogItems = [Defect("APU inoperative"), Defect("Galley oven 2 inoperative")],
        });

        Assert.DoesNotContain("tech log", clean, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(clean, withItems, StringComparison.Ordinal);
        Assert.EndsWith("Open tech log items: APU inoperative, and Galley oven 2 inoperative.", withItems, StringComparison.Ordinal);
        Assert.Equal("tech-log", BriefingComposer.Sections(Departure() with { TechLogItems = [Defect("APU inoperative")] })[^1].Key);
    }

    [Fact]
    public void TechLogItems_ReachTheFactBlock_AndTheirDigitsAreAllowedNumbers()
    {
        var facts = Departure() with { TechLogItems = [Defect("Engine 1 anti-ice valve, ref 302")] };

        var block = BriefingComposer.FactBlock(facts);
        Assert.Contains("- Open tech log items (deferred defects): Engine 1 anti-ice valve, ref 302 (MEL category C)", block, StringComparison.Ordinal);

        // The verifier must not reject the model for repeating a locked title.
        var (offending, _) = BriefingComposer.VerifyNumbers(
            "Note the open tech log item, engine one anti-ice valve reference 302.", facts);
        Assert.Empty(offending);
    }

    // ---- Issue #180: the emergency half of the takeoff briefing and the go-around additions ----

    private static EmergencyBriefFacts Emergency() => new(
        Surface: Core.Weather.RunwaySurfaceState.Dry, Thunderstorms: false, MsaFt: 3500,
        AccelerationAltitudeFt: 1500, ThrustReductionAltitudeFt: 1500, AltitudesAboveAirport: false,
        EngineOutProcedure: EmergencyBriefFacts.DefaultEngineOutProcedure, EngineOutIsPublished: false,
        ReturnRunway: "16R", ThrustMode: "FLEX 52", CeilingFt: null, IcingLikely: false, AirportElevationFt: 21);

    [Fact]
    public void EmergencySection_IsTheTailOfTheDeparture_AndAbsentWhenOff()
    {
        var clean = BriefingComposer.Template(Departure());
        var facts = Departure() with { TechLogItems = [Defect("APU inoperative")], Emergency = Emergency() };

        var text = BriefingComposer.Template(facts);
        var tail = BriefingComposer.Sections(facts).Select(s => s.Key).SkipWhile(k => k != "tech-log").ToArray();

        Assert.DoesNotContain("stop", clean, StringComparison.Ordinal);
        Assert.StartsWith(clean, text, StringComparison.Ordinal);
        Assert.Equal(["tech-log", "rto-split", "efato", "accel-alt", "msa", "return", "threats"], tail);
        Assert.Contains(
            "Below one hundred knots, we stop for any ECAM caution or master warning, engine failure, fire, or if the aircraft "
            + "is unsafe to fly. Above one hundred knots, we stop only for engine failure, fire, or if the aircraft will not fly. "
            + "Engine failure after V1: continue the takeoff, rotate at V R, climb at V2, positive climb gear up, at four hundred "
            + "feet ECAM actions. Engine-out routing: straight ahead, runway heading, to the acceleration altitude. "
            + "Acceleration altitude 1500 feet, thrust reduction 1500 feet. Minimum safe altitude 3500 feet. "
            + "In case of return, YSSY runway 16R.", text, StringComparison.Ordinal);
        // One open tech-log item is a threat of the day, named as a count — the tech-log clause
        // before it carries the title.
        Assert.EndsWith("Threats today: one open tech log item.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void EmergencySection_OmitsUnknownFacts_AndSaysAboveTheAirportWithoutAnElevation()
    {
        var facts = Departure() with
        {
            Emergency = Emergency() with { MsaFt = null, ReturnRunway = null, AltitudesAboveAirport = true, AirportElevationFt = null },
        };

        var keys = BriefingComposer.Sections(facts).Select(s => s.Key).ToArray();
        var text = BriefingComposer.Template(facts);

        Assert.DoesNotContain("msa", keys);
        Assert.DoesNotContain("return", keys);
        Assert.Contains("Acceleration altitude 1500 feet above the airport, thrust reduction 1500 feet above the airport.", text, StringComparison.Ordinal);
        Assert.EndsWith("No specific threats.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void EmergencySection_SpeaksThePublishedEosid_AndTheReturnOnTheAirportName()
    {
        var facts = Departure() with
        {
            AirportName = "Sydney",
            Emergency = Emergency() with { EngineOutProcedure = "heading one six three, climb to 1500 feet; left turn, direct SYD, climb to 3000 feet", EngineOutIsPublished = true },
        };

        var text = BriefingComposer.Template(facts);

        Assert.Contains("Engine-out routing: heading one six three, climb to 1500 feet; left turn, direct SYD, climb to 3000 feet.", text, StringComparison.Ordinal);
        Assert.Contains("In case of return, Sydney runway 16R.", text, StringComparison.Ordinal);
        Assert.Contains("- Engine-out routing (published EOSID): heading one six three", BriefingComposer.FactBlock(facts), StringComparison.Ordinal);
    }

    [Fact]
    public void ThreatList_ComposesEveryItemInFixedOrder_AndNothingWhenTheDayIsClean()
    {
        var facts = Departure() with
        {
            VisibilityM = 800,
            TechLogItems = [Defect("APU inoperative"), Defect("Galley oven 2 inoperative")],
        };
        var emergency = Emergency() with
        {
            Surface = Core.Weather.RunwaySurfaceState.Wet, Thunderstorms = true, CeilingFt = 400, IcingLikely = true,
            MsaFt = 9000, AirportElevationFt = 21,
        };

        var items = ThreatListBuilder.Build(facts, emergency);

        Assert.Equal(
        [
            "wet runway", "thunderstorms in the vicinity", "low visibility, 800 metres", "low cloud, ceiling 400 feet",
            "icing conditions, engine anti-ice", "2 open tech log items", "terrain, minimum safe altitude 9000 feet",
        ], items);
        Assert.Equal("contaminated runway", ThreatListBuilder.Build(Departure(), Emergency() with { Surface = Core.Weather.RunwaySurfaceState.Contaminated })[0]);
        Assert.Empty(ThreatListBuilder.Build(Departure(), Emergency()));
        // MSA 3500 over a 21 ft airport is no terrain threat; 9000 over 4500 is not either (margin 5000).
        Assert.False(ThreatListBuilder.IsTerrainThreat(Emergency()));
        Assert.False(ThreatListBuilder.IsTerrainThreat(Emergency() with { MsaFt = 9000, AirportElevationFt = 4500 }));
        Assert.True(ThreatListBuilder.IsTerrainThreat(Emergency() with { MsaFt = 9600, AirportElevationFt = 4500 }));
        Assert.EndsWith(
            "Threats today: wet runway, thunderstorms in the vicinity, low visibility, 800 metres, low cloud, ceiling 400 feet, "
            + "icing conditions, engine anti-ice, 2 open tech log items, and terrain, minimum safe altitude 9000 feet.",
            BriefingComposer.Template(facts with { Emergency = emergency }), StringComparison.Ordinal);
    }

    [Fact]
    public void AllowedNumbers_IncludeEveryEmergencyAndGoAroundFigure()
    {
        var departure = Departure() with
        {
            Emergency = Emergency() with { CeilingFt = 400, EngineOutProcedure = "heading one six three, climb to 1600 feet; direct SYD, climb to 3000 feet", EngineOutIsPublished = true, AccelerationAltitudeFt = 1500, ThrustReductionAltitudeFt = 1800 },
        };
        var allowed = BriefingComposer.AllowedNumbers(departure);
        foreach (var expected in new double[] { 100, 400, 1500, 1800, 3500, 21, 1600, 3000 })
        {
            Assert.Contains(expected, allowed);
        }

        // The model repeating the fixed phraseology ("one hundred knots", "four hundred feet") verifies.
        var (offending, _) = BriefingComposer.VerifyNumbers(
            "Below 100 knots we stop for anything; above 100 knots only for engine failure or fire. At 400 feet, ECAM actions. "
            + "Acceleration altitude 1500 feet, thrust reduction 1800. MSA 3500 feet.", departure);
        Assert.Empty(offending);

        var arrival = Departure() with { IsDeparture = false, GoAround = new GoAroundBriefFacts(3000, 3500, ["climb to 600 feet", "left turn, direct SOSIJ, climb to 3000 feet"]) };
        var arrivalAllowed = BriefingComposer.AllowedNumbers(arrival);
        Assert.Contains(3000, arrivalAllowed);
        Assert.Contains(3500, arrivalAllowed);
        Assert.Contains(600, arrivalAllowed);
    }

    [Fact]
    public void ArrivalGoAroundClauses_FollowTheMinima_AndUseTheRebriefLegWording()
    {
        var legs = new[] { "climb to 600 feet", "left turn, direct SOSIJ, climb to 3000 feet" };
        var facts = Departure() with
        {
            IsDeparture = false, Approach = "I16RY", Minima = new ArrivalMinima(ArrivalMinimumKind.DecisionAltitude, 320),
            GoAround = new GoAroundBriefFacts(3000, 3500, legs),
        };

        var keys = BriefingComposer.Sections(facts).Select(s => s.Key).SkipWhile(k => k != "minimums").ToArray();
        var text = BriefingComposer.Template(facts);

        Assert.Equal(["minimums", "missed-approach-altitude", "msa", "go-around"], keys);
        Assert.EndsWith(
            "Missed approach altitude 3000 feet. Minimum safe altitude 3500 feet. "
            + "Go around: go around, flaps, positive climb gear up, navigation, climb to 600 feet; left turn, direct SOSIJ, climb to 3000 feet.",
            text, StringComparison.Ordinal);
        // The re-brief speaks "Missed approach. " + the same run, capitalised — one helper, one wording.
        Assert.Equal("climb to 600 feet; left turn, direct SOSIJ, climb to 3000 feet", BriefingComposer.MissedApproachLegsText(legs));

        var unpublished = BriefingComposer.Template(facts with { GoAround = new GoAroundBriefFacts(null, null, []) });
        Assert.EndsWith("Minimums, decision altitude three two zero feet. Go around: go around, flaps, positive climb gear up, navigation, missed approach as published.", unpublished, StringComparison.Ordinal);
        Assert.DoesNotContain("Go around", BriefingComposer.Template(facts with { GoAround = null }), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1500, 21.0, 1500)]
    [InlineData(1500, 1340.0, 2800)]
    [InlineData(1000, 1340.0, 2300)]
    [InlineData(1500, null, 1500)]
    public void BriefedAltitude_AddsTheElevationRoundedToTheHundred_OrStaysAal(int aal, double? elevation, int expected)
        => Assert.Equal(expected, BriefingService.BriefedAltitude(aal, elevation));

    [Fact]
    public void ArrivalTechLogClause_UsesTheLandingWording()
    {
        var facts = Departure() with
        {
            IsDeparture = false,
            TechLogItems = [Defect("Autobrake LO mode inoperative")],
        };

        Assert.EndsWith("Open tech log items affecting landing: Autobrake LO mode inoperative.", BriefingComposer.Template(facts), StringComparison.Ordinal);
        Assert.Contains("- Open tech log items affecting landing: Autobrake LO mode inoperative (MEL category C)", BriefingComposer.FactBlock(facts), StringComparison.Ordinal);
    }
}
