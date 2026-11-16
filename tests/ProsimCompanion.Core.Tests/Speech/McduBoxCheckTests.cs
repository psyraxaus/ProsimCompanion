using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Commands;
using ProsimCompanion.Core.Commands.Handlers;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Mcdu;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>
/// The FO's MCDU check ("check the box", D-I-F-S-R-I-P-P). The screens are the owner's rig as
/// captured on 2026-11-15 (Speech/Fixtures/mcdu-egll-lppt-2026-11-15.txt: BAW504 EGLL-LPPT at
/// the gate, every page key pressed through the ProSim gateway) — the page readers, the
/// judgement, the spoken report, the page walk and the fix offers all run against them.
/// </summary>
public sealed class McduBoxCheckTests
{
    private static readonly DateOnly CaptureDay = new(2026, 11, 15);

    // ---- the page readers, on the captured screens ----

    [Fact]
    public void AircraftStatus_ReadsTheTypeTheEngineAndTheDatabaseCycle()
    {
        var facts = BoxCheckPages.AircraftStatus(Capture.Page("a/c status"));
        Assert.NotNull(facts);
        Assert.Equal("A320-200", facts.AircraftType);
        Assert.Equal("V2527-A5", facts.Engine);
        Assert.Equal(new DateOnly(2026, 10, 29), facts.DatabaseFrom);
        Assert.Equal(new DateOnly(2026, 11, 25), facts.DatabaseTo);
    }

    [Fact]
    public void TheMonitors_ReadThreeIrsInNavAndTwoGpsReceivers()
    {
        Assert.Equal(["NAV", "NAV", "NAV"], BoxCheckPages.IrsMonitor(Capture.Page("irs monitor"))!.Modes);
        Assert.Equal([("NAV", 6), ("NAV", 6)], BoxCheckPages.GpsMonitor(Capture.Page("gps monitor"))!.Receivers);
    }

    [Fact]
    public void InitA_ReadsEveryField()
    {
        var facts = BoxCheckPages.InitA(Capture.Page("init a"));
        Assert.Equal(new InitAFacts("EGLL", "LPPT", "LPFR", "BAW504", 5, 370), facts);
    }

    [Fact]
    public void InitB_ReadsTheWeightsAndTheBlockFuel()
    {
        var facts = BoxCheckPages.InitB(Capture.Page("init b"));
        Assert.Equal(new InitBFacts(53.9, 25.3, 8.5), facts);
    }

    [Fact]
    public void RadNav_ReadsTheIls()
        => Assert.Equal(new RadNavFacts("IRR", "110.30"), BoxCheckPages.RadNav(Capture.Page("rad nav")));

    [Fact]
    public void PerfTakeoff_ReadsWhatIsEntered_AndLeavesTheEmptyFieldsNull()
    {
        var facts = BoxCheckPages.PerfTakeoff(Capture.Page("perf takeoff"));
        Assert.NotNull(facts);
        Assert.Equal("27R", facts.Runway);
        Assert.Null(facts.V1);
        Assert.Null(facts.Vr);
        Assert.Null(facts.V2);
        Assert.Null(facts.Flaps);
        Assert.False(facts.ThsEntered);
        Assert.Null(facts.FlexTempC);
        Assert.Equal(6000, facts.TransitionAltitudeFt);
        Assert.Equal(1578, facts.ThrustReductionFt);
        Assert.Equal(1578, facts.AccelerationFt);
    }

    [Fact]
    public void PerfTakeoff_ReadsEnteredSpeedsFlapsTrimAndFlex()
    {
        var entered = Capture.Xml("perf takeoff")
            .Replace("<line>a###w     F=g143w           </line>", "<line>c141w     F=g143w           </line>", StringComparison.Ordinal)
            .Replace("<line>a###w     S=g187w   [sMl]c[  ]*w</line>", "<line>c145w     S=g187w   [sMl]c[  ]*w</line>", StringComparison.Ordinal)
            .Replace("<line>a###w     O=g205w   c[]/[   ]w</line>", "<line>c149w     O=g205w   c1/UP0.5w</line>", StringComparison.Ordinal)
            .Replace("<line>cs6000lw               c[  ]°w</line>", "<line>cs6000lw               cF52°w</line>", StringComparison.Ordinal);
        var facts = BoxCheckPages.PerfTakeoff(McduDisplayParser.Parse(entered));
        Assert.NotNull(facts);
        Assert.Equal((141, 145, 149), (facts.V1, facts.Vr, facts.V2));
        Assert.Equal(1, facts.Flaps);
        Assert.True(facts.ThsEntered);
        Assert.Equal(52, facts.FlexTempC);
    }

    [Fact]
    public void Prog_ReadsGpsPrimaryAndTheAccuracy()
        => Assert.Equal(new ProgFacts(true, "HIGH"), BoxCheckPages.Prog(Capture.Page("prog")));

    [Fact]
    public void TheSecondaryPlan_OfTheCapture_IsEmpty_AndTheActivePlanIsNot()
    {
        Assert.True(BoxCheckPages.SecondaryIsEmpty(Capture.Page("sec f-pln")));
        Assert.False(BoxCheckPages.SecondaryIsEmpty(Capture.Page("f-pln top")));
    }

    [Fact]
    public void AReader_GivenAnotherPage_ReturnsNull()
    {
        var other = Capture.Page("data index");
        Assert.Null(BoxCheckPages.AircraftStatus(other));
        Assert.Null(BoxCheckPages.IrsMonitor(other));
        Assert.Null(BoxCheckPages.GpsMonitor(other));
        Assert.Null(BoxCheckPages.InitA(other));
        Assert.Null(BoxCheckPages.InitB(other));
        Assert.Null(BoxCheckPages.RadNav(other));
        Assert.Null(BoxCheckPages.PerfTakeoff(other));
        Assert.Null(BoxCheckPages.Prog(other));
    }

    [Fact]
    public void TheDataIndex_LinksAreFoundByTheirText_AndTheMonitorsByTheirLabel()
    {
        var index = Capture.Page("data index");
        Assert.True(BoxCheckPages.IsDataIndex(index));
        Assert.Equal(4, BoxCheckPages.LinkRow(index, "A/C STATUS"));
        Assert.Equal(2, BoxCheckPages.LinkRow(index, "MONITOR", "IRS"));
        Assert.Equal(3, BoxCheckPages.LinkRow(index, "MONITOR", "GPS"));
        Assert.Null(BoxCheckPages.LinkRow(index, "COPY ACTIVE"));
        Assert.Equal(2, BoxCheckPages.LinkRow(Capture.Page("sec index"), "SEC F-PLN"));
    }

    // ---- the F-PLN scroll ----

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void TheScrolledPlan_JoinsOnTheOverlap_AndFindsBothGaps(int pressesPerRead)
    {
        var lines = new List<string>(BoxCheckPages.LegLines(Capture.Scroll(0)));
        for (var i = pressesPerRead; i <= 24; i += pressesPerRead)
        {
            BoxCheckPages.Merge(lines, BoxCheckPages.LegLines(Capture.Scroll(i)));
        }

        var plan = BoxCheckPages.FlightPlan(lines);
        Assert.Equal("EGLL", plan.Origin);
        Assert.Equal("27R", plan.DepartureRunway);
        Assert.True(plan.EndReached);
        Assert.Equal([new FlightPlanGap("GOGSI", "GASGU"), new FlightPlanGap("XAMAX", "LPPT")], plan.Gaps);
    }

    [Fact]
    public void Merge_WithNoOverlap_TakesEveryLine_AndWithTheSameScreen_AddsNone()
    {
        var lines = new List<string> { "A", "B", "C" };
        Assert.Equal(0, BoxCheckPages.Merge(lines, ["A", "B", "C"]));
        Assert.Equal(2, BoxCheckPages.Merge(lines, ["X", "Y"]));
        Assert.Equal(["A", "B", "C", "X", "Y"], lines);
    }

    [Fact]
    public void ThePlan_StopsAtTheEndMarker_TheAlternatePlanIsNotJudged()
    {
        var plan = BoxCheckPages.FlightPlan(["EGLL27R 0000", "GOGSI 0007", "LPPT 0226", "------END OF F-PLN------", "LPPT ----", "---F-PLN DISCONTINUITY--", "LPFR ----"]);
        Assert.Empty(plan.Gaps);
        Assert.True(plan.EndReached);
        Assert.Equal(3, plan.Legs);
    }

    // ---- the judgement ----

    [Fact]
    public void InitA_AgainstThePlan_FindsEachDifference_AndOffersWhatItCanEnter()
    {
        var ofp = new OfpData { OriginIcao = "EGLL", DestinationIcao = "LPPT", AlternateIcao = "LPPR", Callsign = "BAW504", CostIndex = "45", CruiseFlightLevel = 390 };
        var result = BoxCheckEvaluator.InitA(new InitAFacts("EGLL", "LPPT", "LPFR", "BAW504", 5, 370), Reference(ofp: ofp));

        Assert.Equal(BoxCheckItemState.Difference, result.Item.State);
        Assert.Collection(
            result.Findings,
            f =>
            {
                Assert.Equal("ALTN LPFR — the plan has LPPR", f.Text);
                Assert.Null(f.Fix); // an airport is the captain's to change
            },
            f =>
            {
                Assert.Equal("Cost index is five, the plan says four five.", f.Spoken);
                Assert.Equal(new BoxCheckFix(BoxCheckFixKind.CostIndex, "45", "Shall I enter cost index four five?"), f.Fix);
            },
            f =>
            {
                Assert.Equal("CRZ FL370 — the plan has FL390", f.Text);
                Assert.Equal(BoxCheckFixKind.CruiseLevel, f.Fix!.Kind);
                Assert.Equal("390", f.Fix.Value);
            });
    }

    [Fact]
    public void InitA_ThatMatchesThePlan_IsOk_AndWithoutAPlan_IsReadAndNotJudged()
    {
        var ofp = new OfpData { OriginIcao = "EGLL", DestinationIcao = "LPPT", AlternateIcao = "LPFR", AirlineIcao = "BAW", FlightNumber = "504", CostIndex = "5", CruiseFlightLevel = 370 };
        var facts = new InitAFacts("EGLL", "LPPT", "LPFR", "BAW504", 5, 370);
        Assert.Equal(BoxCheckItemState.Ok, BoxCheckEvaluator.InitA(facts, Reference(ofp: ofp)).Item.State);
        Assert.Equal(BoxCheckItemState.Ok, BoxCheckEvaluator.InitA(facts, Reference()).Item.State);
        Assert.Equal(BoxCheckItemState.NotChecked, BoxCheckEvaluator.InitA(null, Reference()).Item.State);
    }

    [Fact]
    public void InitA_AWrongCityPair_IsReported_AndNeverOfferedAsAnEntry()
    {
        var ofp = new OfpData { OriginIcao = "EGLL", DestinationIcao = "LIRF" };
        var finding = Assert.Single(BoxCheckEvaluator.InitA(new InitAFacts("EGLL", "LPPT", null, "BAW504", 5, 370), Reference(ofp: ofp)).Findings);
        Assert.Equal(BoxCheckItemState.Difference, finding.State);
        Assert.Equal("FROM/TO EGLL/LPPT — the plan is EGLL/LIRF", finding.Text);
        Assert.Null(finding.Fix);
    }

    [Fact]
    public void InitB_AgainstTheLoadsheet_UsesTheTolerance_AndOffersTheSync()
    {
        var sheet = LoadsheetSnapshot.EmptySlot with { Status = LoadsheetSlotStatus.Sent, ZfwKg = 54_700, MacZfw = 25.4 };
        var off = BoxCheckEvaluator.InitB(new InitBFacts(53.9, 25.3, 8.5), Reference(loadsheet: sheet));
        var finding = Assert.Single(off.Findings);
        Assert.Equal("ZFW 53.9 — the final loadsheet has 54.7", finding.Text);
        Assert.Equal(BoxCheckFixKind.InitB, finding.Fix!.Kind);

        var close = sheet with { ZfwKg = 54_050 };
        Assert.Equal(BoxCheckItemState.Ok, BoxCheckEvaluator.InitB(new InitBFacts(53.9, 25.3, 8.5), Reference(loadsheet: close)).Item.State);
    }

    [Fact]
    public void InitB_InPounds_IsComparedInPounds()
    {
        // ProSim's Units → Weight option: 53.9 t is 118.8 on a box that shows thousands of pounds.
        var sheet = LoadsheetSnapshot.EmptySlot with { Status = LoadsheetSlotStatus.Sent, ZfwKg = 53_900, MacZfw = 25.3 };
        Assert.Equal(BoxCheckItemState.Ok, BoxCheckEvaluator.InitB(new InitBFacts(118.8, 25.3, 18.7), Reference(loadsheet: sheet)).Item.State);
    }

    [Fact]
    public void InitB_NotEntered_IsNotAFault_AndWithoutALoadsheet_NothingIsOffered()
    {
        var result = BoxCheckEvaluator.InitB(new InitBFacts(null, null, null), Reference());
        Assert.Equal(BoxCheckItemState.NotEntered, result.Item.State);
        Assert.All(result.Findings, f => Assert.Null(f.Fix));
    }

    [Fact]
    public void Perf_OfTheCapture_IsNotEntered_AndWithComputedFigures_OffersThem()
    {
        var facts = BoxCheckPages.PerfTakeoff(Capture.Page("perf takeoff"));
        var bare = BoxCheckEvaluator.Perf(facts, Reference());
        Assert.Equal(BoxCheckItemState.NotEntered, bare.Item.State);
        Assert.Equal(["the V speeds", "the flaps", "the trim"], bare.Findings.Select(f => f.Spoken));
        Assert.All(bare.Findings, f => Assert.Null(f.Fix));

        var computed = new TakeoffPerfSnapshot(1, 52, 141, 145, 149, 62_200, "27R", DateTimeOffset.UtcNow);
        var withFigures = BoxCheckEvaluator.Perf(facts, Reference(perf: computed));
        var fix = withFigures.Findings.First(f => f.Spoken == "the V speeds").Fix;
        Assert.Equal("Shall I enter the computed figures: V one one four one, V R one four five, V two one four niner, flex five two?", fix!.Prompt);
        Assert.Contains(withFigures.Findings, f => f.Spoken == "the flex temperature");
    }

    [Fact]
    public void Perf_EnteredFiguresThatDiffer_AreDifferences_AndAnotherRunwayIsOne()
    {
        var facts = new PerfTakeoffFacts("27L", 138, 145, 149, 2, true, 52, 6000, 1578, 1578);
        var computed = new TakeoffPerfSnapshot(1, 52, 141, 145, 149, 62_200, "27R", DateTimeOffset.UtcNow);
        var result = BoxCheckEvaluator.Perf(facts, Reference(perf: computed));
        Assert.Equal(BoxCheckItemState.Difference, result.Item.State);
        Assert.Equal(
            ["V1 138 — the computed figure is 141", "FLAPS 2 — the computed configuration is 1", "TAKE OFF RWY 27L — the takeoff figures are for 27R"],
            result.Findings.Select(f => f.Text));
    }

    [Fact]
    public void Data_AnExpiredDatabase_AnIrsNotInNav_AreDifferences()
    {
        var status = new AircraftStatusFacts("A320-200", "V2527-A5", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 28), "01OCT26-28OCT26");
        var result = BoxCheckEvaluator.Data(status, new IrsMonitorFacts(["NAV", "ALIGN", "NAV"]), new GpsMonitorFacts([("NAV", 6), ("NAV", 6)]), Reference());
        Assert.Equal(["The navigation database is out of date.", "I R S two is not in nav."], result.Findings.Select(f => f.Spoken));

        var valid = BoxCheckPages.AircraftStatus(Capture.Page("a/c status"));
        Assert.Equal(BoxCheckItemState.Ok, BoxCheckEvaluator.Data(valid, new IrsMonitorFacts(["NAV", "NAV", "NAV"]), null, Reference()).Item.State);
    }

    [Fact]
    public void TheDepartureRunway_IsComparedWithTheTakeoffFigures_NotWithThePlan()
    {
        var plan = new FlightPlanFacts("EGLL", "27R", [], true, 20);
        var computed = new TakeoffPerfSnapshot(1, null, 141, 145, 149, null, "27L", DateTimeOffset.UtcNow);
        var ofp = new OfpData { PlannedRunwayOut = "09R" }; // the planner's guess, not a reference
        Assert.Equal(BoxCheckItemState.Ok, BoxCheckEvaluator.FlightPlan(plan, Reference(ofp: ofp)).Item.State);
        var finding = Assert.Single(BoxCheckEvaluator.FlightPlan(plan, Reference(perf: computed)).Findings);
        Assert.Equal("Departure runway 27R — the takeoff figures are for 27L", finding.Text);
    }

    // ---- the report ----

    [Fact]
    public void Report_WithNothingToSay_IsOneLine()
        => Assert.Equal("Box checked, no differences.", BoxCheckEvaluator.Report([], []));

    [Fact]
    public void Report_SaysTheDifferencesAsSentences_ThenWhatIsNotEntered_ThenWhatWasNotRead()
    {
        BoxCheckFinding[] findings =
        [
            new("fpln", BoxCheckItemState.Difference, "", "A discontinuity after GOGSI."),
            new("sec", BoxCheckItemState.NotEntered, "", "the secondary flight plan"),
            new("perf", BoxCheckItemState.NotEntered, "", "the V speeds"),
            new("perf", BoxCheckItemState.NotEntered, "", "the trim"),
        ];
        Assert.Equal(
            "Box checked, one difference. A discontinuity after GOGSI. Not entered yet: the secondary flight plan, the V speeds and the trim. I could not read the progress page.",
            BoxCheckEvaluator.Report(findings, [BoxCheckEvaluator.SpokenItem(BoxCheckItemIds.Prog)]));
    }

    // ---- the run, on the captured box ----

    [Fact]
    public async Task TheCheck_Acknowledges_WalksAllEightPageGroups_AndReportsWhatTheCapturedBoxHolds()
    {
        var rig = new Rig();
        await rig.Service.RunAsync("test", new CancellationTokenSource());

        var spoken = rig.Spoken();
        Assert.Equal("Checking the box.", spoken[0]);
        Assert.Equal(
            "Box checked, two differences. A discontinuity after GOGSI. A discontinuity after XAMAX. "
            + "Not entered yet: the secondary flight plan, the V speeds, the flaps and the trim.",
            spoken[1]);

        var items = rig.Store.Snapshot().Items.ToDictionary(i => i.Id, i => i.State);
        Assert.Equal(BoxCheckItemState.Ok, items[BoxCheckItemIds.Data]);
        Assert.Equal(BoxCheckItemState.Ok, items[BoxCheckItemIds.InitA]);
        Assert.Equal(BoxCheckItemState.Difference, items[BoxCheckItemIds.FlightPlan]);
        Assert.Equal(BoxCheckItemState.NotEntered, items[BoxCheckItemIds.Secondary]);
        Assert.Equal(BoxCheckItemState.Ok, items[BoxCheckItemIds.RadNav]);
        Assert.Equal(BoxCheckItemState.Ok, items[BoxCheckItemIds.InitB]); // the old macro never reached this page
        Assert.Equal(BoxCheckItemState.NotEntered, items[BoxCheckItemIds.Perf]);
        Assert.Equal(BoxCheckItemState.Ok, items[BoxCheckItemIds.Prog]);
        Assert.False(rig.Store.Snapshot().Running);
        Assert.False(rig.Service.IsRunning);
    }

    [Fact]
    public async Task TheCheck_WithActuationOff_PressesOnlyPageKeysArrowsAndCheckedLinks_AndEndsOnTheFlightPlan()
    {
        var rig = new Rig();
        await rig.Service.RunAsync("test", new CancellationTokenSource());

        Assert.Empty(rig.Box.ArmedPresses);
        Assert.All(rig.Box.Navigated, suffix => Assert.True(McduControls.IsNavigationSuffix(suffix), suffix));
        Assert.Equal(["A/C STATUS", "MONITOR", "MONITOR", "SEC F-PLN"], rig.Box.Links);
        Assert.Equal("FPLN", rig.Box.Navigated[^1]);
        Assert.Equal(
            ["DATA", "DATA", "DATA", "INIT", "FPLN", "FPLN", "SEC_FPLN", "RAD_NAV", "INIT", "ARROW_RIGHT", "PERF", "PROG", "FPLN"],
            rig.Box.Navigated.Where(s => s != "ARROW_UP"));

        // No fix that needs keys is asked for with actuation off; the FO says so once.
        Assert.Empty(rig.Asked);
        Assert.Contains("One item needs M C D U actuation, which is off — that one is yours.", rig.Spoken());
    }

    [Fact]
    public async Task AYes_ToTheSecondaryOffer_CopiesTheActivePlan_WhenActuationIsOn()
    {
        var rig = new Rig { Armed = true, Answer = true };
        await rig.Service.RunAsync("test", new CancellationTokenSource());

        Assert.Equal(["The secondary flight plan is empty. Shall I copy the active plan?"], rig.Asked);
        Assert.Equal(["LSK1L"], rig.Box.ArmedPresses);
        Assert.Contains("Active plan copied to the secondary.", rig.Spoken());
    }

    [Fact]
    public async Task ANo_LeavesTheBoxAlone()
    {
        var rig = new Rig { Armed = true, Answer = false };
        await rig.Service.RunAsync("test", new CancellationTokenSource());

        Assert.Single(rig.Asked);
        Assert.Empty(rig.Box.ArmedPresses);
    }

    [Fact]
    public async Task WithOfferFixesOff_TheFoReportsOnly()
    {
        var rig = new Rig { Armed = true, Answer = true };
        rig.Options.BoxCheckOfferFixes = false;
        await rig.Service.RunAsync("test", new CancellationTokenSource());

        Assert.Empty(rig.Asked);
        Assert.Empty(rig.Box.ArmedPresses);
    }

    [Fact]
    public async Task AYes_ToTheTakeoffFigures_WritesTheDatarefs_WithoutAnyKey()
    {
        var rig = new Rig { Answer = true };
        rig.Perf.Set(new TakeoffPerfSnapshot(1, 52, 141, 145, 149, 62_200, "27R", DateTimeOffset.UtcNow));
        await rig.Service.RunAsync("test", new CancellationTokenSource());

        Assert.Empty(rig.Box.ArmedPresses);
        rig.DataRefs.Verify(d => d.WriteAsync("aircraft.fms.perf.takeOff.v1", 141, It.IsAny<CancellationToken>()), Times.Once);
        rig.DataRefs.Verify(d => d.WriteAsync("aircraft.fms.perf.takeOff.vr", 145, It.IsAny<CancellationToken>()), Times.Once);
        rig.DataRefs.Verify(d => d.WriteAsync("aircraft.fms.perf.takeOff.v2", 149, It.IsAny<CancellationToken>()), Times.Once);
        rig.DataRefs.Verify(d => d.WriteAsync("aircraft.fms.perf.takeOff.flexTemp", 52.0, It.IsAny<CancellationToken>()), Times.Once);
        rig.DataRefs.Verify(d => d.WriteAsync("aircraft.fms.perf.takeOff.flaps", 1, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains("Takeoff figures entered. The trim is yours.", rig.Spoken());
    }

    [Fact]
    public async Task AYes_ToTheCostIndex_ClearsTheMessage_TypesIt_AndReadsTheFieldBack()
    {
        var rig = new Rig { Armed = true };
        rig.Ofp.Set(new OfpData { OriginIcao = "EGLL", DestinationIcao = "LPPT", AlternateIcao = "LPFR", Callsign = "BAW504", CostIndex = "45", CruiseFlightLevel = 370 });
        rig.AnswerFor = prompt => prompt.Contains("cost index", StringComparison.Ordinal);
        await rig.Service.RunAsync("test", new CancellationTokenSource());

        Assert.Contains("Shall I enter cost index four five?", rig.Asked);
        Assert.Equal(["CLEAR", "type:45", "LSK5L"], rig.Box.ArmedPresses);
        Assert.Contains("Cost index entered.", rig.Spoken());
        Assert.Equal(45, BoxCheckPages.InitA(Capture.Parse(rig.Box.InitAXml))!.CostIndex);
    }

    [Fact]
    public void Start_IsRefusedWithoutADisplay_AndTheVoicePhraseSaysWhy()
    {
        var rig = new Rig();
        rig.Box.DisplayAvailable = false;

        Assert.Equal(BoxCheckStart.Unavailable, rig.Service.Start("test"));
        Assert.True(rig.Service.TryHandle("check the box"));
        Assert.Equal(["Unable — I can't see the M C D U. ProSim is not sending its display."], rig.Spoken());
    }

    [Fact]
    public void OtherWords_AreNotTaken_AndStopWithNothingRunningIsNotOurs()
    {
        var rig = new Rig();
        Assert.False(rig.Service.TryHandle("read the box"));
        Assert.False(rig.Service.TryHandle("stop the check"));
        rig.Options.Enabled = false;
        Assert.False(rig.Service.TryHandle("check the box"));
    }

    // ---- small rules ----

    [Theory]
    [InlineData(5, 1, 0.0, 3.75)]
    [InlineData(5, 1, 1.0, 6.25)]
    [InlineData(5, 0.5, 0.5, 2.5)]
    [InlineData(0, 1, 0.5, 0)]
    [InlineData(99, 1, 0.5, 20)]
    public void PageSeconds_IsTheSettingTimesTheShare_AQuarterEitherWay(double configured, double share, double random, double expected)
        => Assert.Equal(expected, McduBoxCheckService.PageSeconds(configured, share, random), 3);

    [Theory]
    [InlineData("yes", true)]
    [InlineData("enter it", true)]
    [InlineData("go ahead", true)]
    [InlineData("no", false)]
    [InlineData("negative", false)]
    [InlineData("leave it", false)]
    [InlineData("don't enter it", false)]
    [InlineData("I'll do it", false)]
    [InlineData("flaps one", null)]
    public void TheFixAnswer_IsYesNoOrNotAnAnswer(string heard, bool? expected)
        => Assert.Equal(expected, BoxCheckFixPhrases.Interpret(heard));

    [Theory]
    [InlineData("DATA", true)]
    [InlineData("ARROW_UP", true)]
    [InlineData("LSK1L", false)]
    [InlineData("CLEAR", false)]
    [InlineData("A", false)]
    [InlineData("5", false)]
    [InlineData(null, false)]
    public void TheNavigationAllowList_HoldsPageKeysAndArrowsOnly(string? suffix, bool expected)
        => Assert.Equal(expected, McduControls.IsNavigationSuffix(suffix));

    [Fact]
    public async Task TheActuator_TurnsPagesWithActuationOff_ButPressesNoOtherKey_AndNoLinkTheScreenDoesNotShow()
    {
        var options = new McduOptions { Enabled = true, AllowActuation = false };
        var reader = new Mock<IMcduReader>();
        reader.SetupGet(r => r.IsDisplayAvailable).Returns(true);
        reader.Setup(r => r.Read()).Returns(Capture.Page("data index"));
        var dataRefs = new Mock<IProsimDataRefs>();
        using var actuator = new McduActuator(dataRefs.Object, reader.Object, Monitor(options), SpeechTestSupport.TempEventLog(), NullLogger<McduActuator>.Instance);

        Assert.True(actuator.CanNavigate);
        Assert.False(actuator.IsArmed);
        Assert.True(await actuator.NavigateAsync("DATA"));
        Assert.False(await actuator.NavigateAsync("LSK1L"));
        Assert.False(await actuator.PressKeyAsync("DATA"));
        Assert.True(await actuator.FollowLinkAsync(4, right: false, "A/C STATUS"));
        Assert.False(await actuator.FollowLinkAsync(5, right: false, "A/C STATUS"));

        dataRefs.Verify(d => d.PressMomentaryAsync("system.switches.S_CDU2_KEY_DATA", It.IsAny<CancellationToken>()), Times.Once);
        dataRefs.Verify(d => d.PressMomentaryAsync("system.switches.S_CDU2_KEY_LSK4L", It.IsAny<CancellationToken>()), Times.Once);
        dataRefs.Verify(d => d.PressMomentaryAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public void TheCommand_MapsTheSeamsAnswer()
    {
        var control = new Mock<IBoxCheckControl>();
        control.SetupSequence(c => c.Start("command")).Returns(BoxCheckStart.Started).Returns(BoxCheckStart.AlreadyRunning).Returns(BoxCheckStart.Unavailable);

        Assert.Equal(CommandOutcome.Success, McduCommandHandlers.CheckBox(control.Object, "command").Outcome);
        Assert.Equal(CommandOutcome.AlreadySatisfied, McduCommandHandlers.CheckBox(control.Object, "command").Outcome);
        Assert.Equal(CommandOutcome.Unavailable, McduCommandHandlers.CheckBox(control.Object, "command").Outcome);
        Assert.Equal(CommandOutcome.Unavailable, McduCommandHandlers.CheckBox(null, "command").Outcome);
    }

    [Fact]
    public void TheFixOffer_IsOneQuestionAtATime_AndAnotherMayFollowAnAnswer()
    {
        using var store = new BoxCheckFixStore(new GroundOpsSignals());
        Assert.True(store.Ask("Shall I enter cost index four five?", "CostIndex"));
        Assert.False(store.Ask("Shall I copy the active plan?", "CopyActive"));
        Assert.True(store.Decline("test", "no"));
        Assert.True(store.Ask("Shall I copy the active plan?", "CopyActive"));
    }

    // ---- support ----

    private static BoxCheckReference Reference(OfpData? ofp = null, LoadsheetSlotView? loadsheet = null, TakeoffPerfSnapshot? perf = null)
        => new(ofp, loadsheet, "final", perf, CaptureDay, icao => icao ?? "");

    private static IOptionsMonitor<McduOptions> Monitor(McduOptions options)
    {
        var monitor = new Mock<IOptionsMonitor<McduOptions>>();
        monitor.SetupGet(m => m.CurrentValue).Returns(() => options);
        return monitor.Object;
    }

    /// <summary>The service over the captured box, with no reading time.</summary>
    private sealed class Rig
    {
        private readonly FakeArbiter _arbiter = new();

        public Rig()
        {
            var signals = new GroundOpsSignals();
            Store = new BoxCheckStore(signals);
            Fixes = new BoxCheckFixStore(signals);
            Fixes.QuestionRaised += (_, question) =>
            {
                Asked.Add(question.Prompt);
                if (AnswerFor?.Invoke(question.Prompt) ?? Answer)
                {
                    Fixes.Accept("test");
                }
                else
                {
                    Fixes.Decline("test", "no");
                }
            };
            Box = new FakeBox(this);
            var clock = new Mock<ISimClock>();
            clock.SetupGet(c => c.UtcNowOrReal).Returns(new DateTimeOffset(2026, 11, 15, 8, 34, 0, TimeSpan.Zero));
            Service = new McduBoxCheckService(
                Box, Box, Monitor(Options), _arbiter, SpeechTestSupport.TempEventLog(), NullLogger<McduBoxCheckService>.Instance,
                Store, Fixes, Ofp, new LoadsheetStore(), Perf, clock.Object, new ProsimCompanion.Core.Speech.SpokenText(), DataRefs.Object);
        }

        public McduOptions Options { get; } = new() { Enabled = true, BoxCheckPageSeconds = 0 };

        public bool Armed { get; init; }

        public bool Answer { get; init; }

        public Func<string, bool>? AnswerFor { get; set; }

        public List<string> Asked { get; } = [];

        public BoxCheckStore Store { get; }

        public BoxCheckFixStore Fixes { get; }

        public OfpStore Ofp { get; } = new();

        public TakeoffPerfStore Perf { get; } = new();

        public Mock<IProsimDataRefs> DataRefs { get; } = new();

        public FakeBox Box { get; }

        public McduBoxCheckService Service { get; }

        public IReadOnlyList<string> Spoken()
        {
            lock (_arbiter.Requests)
            {
                return [.. _arbiter.Requests.Select(r => r.Text)];
            }
        }
    }

    /// <summary>The captured MCDU as a small state machine: a page key shows its captured page,
    /// the slew key steps through the 33 captured F-PLN screens, a link opens its sub-page.</summary>
    private sealed class FakeBox(Rig rig) : IMcduReader, IMcduActuator
    {
        private string _page = "f-pln top";
        private int _scroll;
        private string _scratchpad = "WIND DATA UPLINK";
        private string? _typed;
        private bool _copied;

        public bool DisplayAvailable { get; set; } = true;

        public List<string> Navigated { get; } = [];

        public List<string> Links { get; } = [];

        public List<string> ArmedPresses { get; } = [];

        public string InitAXml { get; private set; } = Capture.Xml("init a");

        public bool IsDisplayAvailable => DisplayAvailable;

        public bool CanNavigate => DisplayAvailable && rig.Options.Enabled;

        public bool IsArmed => CanNavigate && rig.Armed;

        public McduPage Read()
        {
            var xml = _page switch
            {
                "f-pln" => Capture.ScrollXml(_scroll),
                "init a" => InitAXml,
                "sec f-pln" when _copied => Capture.Xml("f-pln top"),
                _ => Capture.Xml(_page),
            };
            return McduDisplayParser.Parse(Regex.Replace(xml, "<scratchpad>.*?</scratchpad>", $"<scratchpad>{_scratchpad}</scratchpad>"));
        }

        public Task<McduPage> WaitSettledAsync(CancellationToken cancellationToken = default) => Task.FromResult(Read());

        public Task<bool> NavigateAsync(string suffix, CancellationToken cancellationToken = default)
        {
            if (!CanNavigate || !McduControls.IsNavigationSuffix(suffix))
            {
                return Task.FromResult(false);
            }

            Navigated.Add(suffix);
            switch (suffix)
            {
                case "DATA": _page = "data index"; break;
                case "INIT": _page = "init a"; break;
                case "ARROW_RIGHT" when _page == "init a": _page = "init b"; break;
                case "ARROW_LEFT" when _page == "init b": _page = "init a"; break;
                case "FPLN": _page = "f-pln"; _scroll = 0; break;
                case "ARROW_UP" when _page == "f-pln": _scroll = (_scroll + 1) % 34; break;
                case "SEC_FPLN": _page = "sec index"; break;
                case "RAD_NAV": _page = "rad nav"; break;
                case "PERF": _page = "perf takeoff"; break;
                case "PROG": _page = "prog"; break;
            }

            return Task.FromResult(true);
        }

        public Task<bool> FollowLinkAsync(int row, bool right, string rowMustContain, CancellationToken cancellationToken = default)
        {
            if (!CanNavigate || !Read().Rows.First(r => r.Row == row).Data.Contains(rowMustContain, StringComparison.Ordinal))
            {
                return Task.FromResult(false);
            }

            Links.Add(rowMustContain);
            _page = (_page, row) switch
            {
                ("data index", 4) => "a/c status",
                ("data index", 1) => "position monitor",
                ("data index", 2) => "irs monitor",
                ("data index", 3) => "gps monitor",
                ("sec index", 2) => "sec f-pln",
                _ => _page,
            };
            return Task.FromResult(true);
        }

        public Task<bool> PressKeyAsync(string suffix, CancellationToken cancellationToken = default)
        {
            if (!IsArmed)
            {
                return Task.FromResult(false);
            }

            if (McduControls.IsNavigationSuffix(suffix))
            {
                return NavigateAsync(suffix, cancellationToken);
            }

            ArmedPresses.Add(suffix);
            if (suffix == "CLEAR")
            {
                _scratchpad = "";
            }
            else if (suffix == "LSK1L" && _page == "sec index")
            {
                _copied = true;
            }
            else if (suffix == "LSK5L" && _page == "init a" && _typed is not null)
            {
                InitAXml = InitAXml.Replace("<line>c5w ", $"<line>c{_typed}w ", StringComparison.Ordinal);
                _scratchpad = "";
                _typed = null;
            }

            return Task.FromResult(true);
        }

        public Task<bool> PressLskAsync(int row, bool right, CancellationToken cancellationToken = default)
            => PressKeyAsync($"LSK{row}{(right ? "R" : "L")}", cancellationToken);

        public Task<bool> TypeAsync(string text, CancellationToken cancellationToken = default)
        {
            if (!IsArmed)
            {
                return Task.FromResult(false);
            }

            ArmedPresses.Add("type:" + text);
            _typed = text;
            _scratchpad += text;
            return Task.FromResult(true);
        }

        public Task<bool> GoToPageAsync(string funcKeySuffix, string expectTitleContains, CancellationToken cancellationToken = default)
            => NavigateAsync(funcKeySuffix, cancellationToken);
    }

    /// <summary>The capture file: "=== KEY | label" then the display XML as ProSim sent it.</summary>
    private static class Capture
    {
        private static readonly Dictionary<string, string> Frames = Load();

        public static string Xml(string label) => Frames[label];

        public static McduPage Page(string label) => Parse(Frames[label]);

        public static McduPage Parse(string xml) => McduDisplayParser.Parse(xml);

        /// <summary>The F-PLN after <paramref name="presses"/> slew presses (0 = the top).</summary>
        public static string ScrollXml(int presses) => Frames[presses == 0 ? "f-pln top" : $"f-pln scroll {presses}"];

        public static McduPage Scroll(int presses) => Parse(ScrollXml(presses));

        private static Dictionary<string, string> Load()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Speech", "Fixtures", "mcdu-egll-lppt-2026-11-15.txt");
            var frames = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var block in File.ReadAllText(path).Split("=== ", StringSplitOptions.RemoveEmptyEntries))
            {
                var newline = block.IndexOf('\n', StringComparison.Ordinal);
                var label = block[..newline].Split('|')[1].Trim();
                frames[label] = block[(newline + 1)..].Trim();
            }

            return frames;
        }
    }
}
