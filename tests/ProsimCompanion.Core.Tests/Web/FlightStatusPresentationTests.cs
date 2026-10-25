using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Boarding;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Core.Tests.Localization;
using ProsimCompanion.Core.Weather;
using ProsimCompanion.Web.Components;
using Xunit;

namespace ProsimCompanion.Core.Tests.Web;

/// <summary>
/// The Flight Status presentation module (architecture review candidate 6): store snapshots
/// in, row records out. One test per row builder pins the tone, the text id and the
/// arguments the page renders; the card tests pin the row order the Prosim2GSX clone has
/// always had; the tile test pins the saved-order rules of the three tile-order commits.
/// </summary>
public sealed class FlightStatusPresentationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 19, 10, 45, 0, TimeSpan.Zero);

    // The words come from the shipped English pack (ADR-0014): a DisplayText resolved here
    // reads exactly what a pilot sees, so a renamed id or a lost text fails here.
    private static readonly IStringLocalizer T = new ServiceCollection().AddTestLocalizer()
        .BuildServiceProvider().CreateScope().ServiceProvider.GetRequiredService<IStringLocalizer>();

    private static readonly Regex Clock = new(@"^\d\d:\d\d:\d\d$", RegexOptions.CultureInvariant);

    private static GsxServiceView Service(string id, GsxServiceStage stage, string? semantic = "available", string mapped = "Available")
        => new(id, id, semantic, mapped, CanTrigger: true, Waiting: false, ProgressText: null, stage);

    private static GsxDiagnosticsSnapshot Gsx(params GsxServiceView[] services)
        => GsxDiagnosticsSnapshot.Empty with { Services = services };

    // ---- DisplayText ----

    [Fact]
    public void DisplayText_ResolvesIdsLiteralsAndNestedArguments()
    {
        Assert.Equal("—", DisplayText.Dash.Resolve(T));
        Assert.Equal("Available", DisplayText.Raw("Available").Resolve(T));
        Assert.Equal("Yes", DisplayText.Id("flightStatus.common.yes").Resolve(T));
        Assert.Equal("3 mismatches", DisplayText.Id("flightStatus.aircraftState.mismatchMany", 3).Resolve(T));
        // The ground-prep tooltip embeds the translated stage word as its first argument.
        Assert.Equal("Repositioning — waiting for the position to settle",
            DisplayText.Id("flightStatus.services.prep.tooltip", DisplayText.Id("flightStatus.services.prep.reposition"), "waiting for the position to settle").Resolve(T));
        Assert.Null(DisplayText.RawOrNull(null));
    }

    // ---- connections and hero ----

    [Fact]
    public void Connections_ReadTheThreeSubsystems()
    {
        var links = FlightStatusPresentation.Connections(
        [
            new(Subsystems.Prosim, ConnectionState.Connected),
            new(Subsystems.Gsx, ConnectionState.Connecting),
        ]);

        Assert.False(links.SimConnected);     // absent = not connected
        Assert.True(links.ProsimConnected);
        Assert.False(links.GsxConnected);     // connecting is not connected
    }

    [Fact]
    public void PhaseReason_AndRouteEnds_KeepTheShape()
    {
        Assert.Equal("—", FlightStatusPresentation.PhaseReason(new FlightStateView(FlightPhase.Preflight, null, false)));
        Assert.Equal("beacon on", FlightStatusPresentation.PhaseReason(new FlightStateView(FlightPhase.Preflight, null, false, LastTransitionReason: "beacon on")));
        Assert.Equal("----", FlightStatusPresentation.RouteEnd(null));
        Assert.Equal("----", FlightStatusPresentation.RouteEnd(""));
        Assert.Equal("EGLL", FlightStatusPresentation.RouteEnd("EGLL"));
    }

    [Fact]
    public void ValidData_HidesAnInvalidSample()
    {
        var invalid = new FlightStateView(FlightPhase.Preflight, new FlightDataSnapshot { IsValid = false, GroundSpeedKt = 12 }, false);
        var valid = new FlightStateView(FlightPhase.Preflight, new FlightDataSnapshot { IsValid = true, GroundSpeedKt = 12 }, false);

        Assert.Null(FlightStatusPresentation.ValidData(invalid));
        Assert.Equal(12, FlightStatusPresentation.ValidData(valid)!.GroundSpeedKt);
    }

    [Fact]
    public void FlightLine_JoinsTheOfpFacts_OrSaysNoPlan()
    {
        Assert.Equal("flightStatus.hero.noFlightPlan", FlightStatusPresentation.FlightLine(null).TextId);

        var full = new OfpData { Ident = "ABCD", Callsign = "BAW123", FlightNumber = "123", CruiseFlightLevel = 360, ScheduledOutUtc = T0 };
        Assert.Equal("BAW123 | FLT 123 | CRZ FL360 | STD 10:45Z", FlightStatusPresentation.FlightLine(full).Literal);

        var bare = new OfpData { Ident = "ABCD" };
        Assert.Equal("ABCD", FlightStatusPresentation.FlightLine(bare).Literal);
    }

    [Fact]
    public void Metrics_FormatTheFiveFigures_InStripOrder()
    {
        var data = new FlightDataSnapshot { IsValid = true, GroundSpeedKt = 245.6, AltitudeFt = 12345.4, VerticalSpeedFpm = -1850.2 };

        var metrics = FlightStatusPresentation.Metrics(data, -10, 6543.2, kg => $"{kg:F0} kg");

        Assert.Equal(["flightStatus.hero.groundSpeed", "flightStatus.hero.altitude", "flightStatus.hero.verticalSpeed", "flightStatus.hero.heading", "flightStatus.hero.fuelOnBoard"],
            metrics.Select(m => m.LabelTextId));
        Assert.Equal(["246", "12,345", "-1,850", "350°", "6543 kg"], metrics.Select(m => m.Figure));
        Assert.Equal(["KTS", "FT", "FPM", "MAG", null], metrics.Select(m => m.Unit));
        Assert.Equal([true, false, true, true, false], metrics.Select(m => m.Glow));
    }

    [Fact]
    public void Metrics_WithoutDataOrProsim_AreDashes()
    {
        var metrics = FlightStatusPresentation.Metrics(null, null, null, kg => "never");

        Assert.All(metrics, m => Assert.Equal("—", m.Figure));
        Assert.Equal("+500", FlightStatusPresentation.Metrics(new FlightDataSnapshot { IsValid = true, VerticalSpeedFpm = 500 }, null, null, kg => "")[2].Figure);
        Assert.Equal("0", FlightStatusPresentation.Metrics(new FlightDataSnapshot { IsValid = true }, null, null, kg => "")[2].Figure);
    }

    [Fact]
    public void Progress_TagsSayWhereEachFigureCameFrom()
    {
        var located = new FlightProgressSnapshot { DistanceToGoNm = 311.6, DistanceFlownNm = 88.2, EtaUtc = T0, EtaBasis = ProgressBasis.Position, MinutesToTod = 14.4 };
        var items = FlightStatusPresentation.Progress(located);

        Assert.Equal(["flightStatus.hero.toGo", "flightStatus.hero.flown", "flightStatus.hero.eta", "flightStatus.hero.topOfDescent"], items.Select(i => i.LabelTextId));
        Assert.Equal(["312 NM", "88 NM", "10:45Z", "14 MIN"], items.Select(i => i.Figure));
        Assert.Equal([null, null, "GS", "EST"], items.Select(i => i.Tag));

        var planned = FlightStatusPresentation.Progress(new FlightProgressSnapshot { EtaUtc = T0, EtaBasis = ProgressBasis.Time });
        Assert.Equal("PLAN", planned[2].Tag);
        Assert.Null(planned[3].Tag);

        var none = FlightStatusPresentation.Progress(FlightProgressSnapshot.Empty);
        Assert.All(none, i => Assert.Null(i.Tag));
        Assert.All(none, i => Assert.Equal("—", i.Figure));
    }

    // ---- hero widgets ----

    [Fact]
    public void Weather_FoundCard_CarriesTheFormattedLines()
    {
        var facts = MetarParser.ToFacts("EGLL 221020Z 25014G24KT 4000 -RA BKN030 17/09 Q1012", "J");
        var card = new WeatherCard(WeatherCardRole.Local, "EGLL", "London Heathrow", WxProbeStatus.Found, facts, SkyCondition.Rain, "RAIN", "10:20Z", null);

        var view = FlightStatusPresentation.Weather(card);

        Assert.Equal("flightStatus.weather.local", view.TitleTextId);
        Assert.Equal("London Heathrow", view.Name.Literal);
        Assert.Equal("J", view.Atis);
        Assert.Equal("sky-rain", view.SkyClass);
        Assert.Equal("cloud-rain", view.SkyIcon);
        Assert.Equal(facts.RawMetar, view.RawTitle);
        Assert.True(view.Found);
        Assert.Equal(250, view.WindArrowDeg);
        Assert.Equal("250°/14 KT", view.Wind);
        Assert.Equal(24, view.Gust);
        Assert.Equal("4 KM", view.Visibility);
        Assert.Equal("CEILING 3,000 FT", view.Ceiling);
        Assert.Equal("+17°C", view.Temperature);
        Assert.Equal("Q1012", view.Qnh);
        Assert.Equal("METAR 10:20Z", view.Stamp);
        Assert.Null(view.Missing);
    }

    [Fact]
    public void Weather_MissingCards_SayWhy()
    {
        var empty = FlightStatusPresentation.Weather(WeatherCard.Empty(WeatherCardRole.Destination));
        Assert.Equal("flightStatus.weather.destination", empty.TitleTextId);
        Assert.Equal("flightStatus.weather.noFlightPlan", empty.Name.TextId);
        Assert.Equal("flightStatus.weather.fetchOfp", empty.Missing!.TextId);
        Assert.Null(empty.Atis);
        Assert.Null(empty.WindArrowDeg);
        Assert.Equal("METAR —", empty.Stamp);

        var failed = FlightStatusPresentation.Weather(new WeatherCard(WeatherCardRole.Alternate, "LFPG", "", WxProbeStatus.Unavailable, WxFacts.None, SkyCondition.Unknown, "NO DATA", null, "weather source offline"));
        Assert.Equal("flightStatus.weather.alternate", failed.TitleTextId);
        Assert.Equal("LFPG", failed.Name.Literal);
        Assert.Equal("weather source offline", failed.Missing!.Literal);
        Assert.Equal("weather source offline", failed.RawTitle);

        var noMetar = FlightStatusPresentation.Weather(new WeatherCard(WeatherCardRole.Destination, "LFPG", "Paris", WxProbeStatus.NoData, WxFacts.None, SkyCondition.Unknown, "NO DATA", null, null));
        Assert.Equal("flightStatus.weather.noMetar", noMetar.Missing!.TextId);
    }

    [Fact]
    public void Weather_CalmWind_HasNoArrow()
    {
        var facts = MetarParser.ToFacts("EGLL 221020Z 00000KT 9999 FEW020 17/09 Q1012");
        var view = FlightStatusPresentation.Weather(new WeatherCard(WeatherCardRole.Local, "EGLL", "", WxProbeStatus.Found, facts, SkyCondition.FewClouds, "FEW CLOUDS", "10:20Z", null));

        Assert.Null(view.WindArrowDeg);
        Assert.Equal("CALM", view.Wind);
        Assert.Null(view.Gust);
        Assert.Equal("EGLL", view.Name.Literal);
    }

    [Theory]
    [InlineData(GateState.Closed, "closed", "flightStatus.gate.closed", "door-closed", false)]
    [InlineData(GateState.Open, "open", "flightStatus.gate.open", "door-open", false)]
    [InlineData(GateState.Boarding, "boarding", "flightStatus.gate.boarding", "door-closed", true)]
    [InlineData(GateState.FinalCall, "final-call", "flightStatus.gate.finalCall", "bell-ring", false)]
    [InlineData(GateState.ClosedAfterBoarding, "closed-after", "flightStatus.gate.closed", "circle-check", false)]
    public void Gate_FiveStates_MapToClassWordAndIcon(GateState state, string cssClass, string textId, string icon, bool dot)
    {
        var view = FlightStatusPresentation.Gate(GateStatusSnapshot.Empty with { State = state });

        Assert.Equal(cssClass, view.StateClass);
        Assert.Equal(textId, view.StateTextId);
        Assert.Equal(icon, view.Icon);
        Assert.Equal(dot, view.BoardingDot);
        Assert.Equal(dot, view.PaxGlow);
        Assert.Equal(state == GateState.FinalCall, view.StdSoon);
    }

    [Fact]
    public void Gate_StdAndPaxAndProgress()
    {
        var boarding = FlightStatusPresentation.Gate(new GateStatusSnapshot(GateState.Boarding, "B12", 60, 150, T0, 12, "Jetway · door 1L", T0.AddMinutes(-3), false));
        Assert.Equal("B12", boarding.GateId);
        Assert.Equal("STD −12 MIN", boarding.Std);
        Assert.Equal(150, boarding.PaxTarget);
        Assert.Equal(60, boarding.PaxBoarded);
        Assert.Equal(40, boarding.ProgressPercent);
        Assert.Equal("40", boarding.FillWidth);
        Assert.Equal("flightStatus.gate.since", boarding.Since!.TextId);
        Assert.Equal("10:42Z", boarding.Since.Arguments[0]);
        Assert.Equal("· since 10:42Z", boarding.Since.Resolve(T));

        var late = FlightStatusPresentation.Gate(GateStatusSnapshot.Empty with { StdUtc = T0, MinutesToStd = -3 });
        Assert.Equal("STD +3 MIN", late.Std);

        var dimmed = FlightStatusPresentation.Gate(GateStatusSnapshot.Empty with { StdUtc = T0, MinutesToStd = -30, Dimmed = true });
        Assert.Equal("STD 10:45Z", dimmed.Std);
        Assert.True(dimmed.Dimmed);

        var closedAfter = FlightStatusPresentation.Gate(GateStatusSnapshot.Empty with { State = GateState.ClosedAfterBoarding, StdUtc = T0, MinutesToStd = 5 });
        Assert.Equal("STD 10:45Z", closedAfter.Std);
        Assert.Equal(100, closedAfter.ProgressPercent);   // full bar once boarding closed

        var empty = FlightStatusPresentation.Gate(GateStatusSnapshot.Empty);
        Assert.Equal("STD —", empty.Std);
        Assert.Equal("—", empty.GateId);
        Assert.Null(empty.PaxTarget);
        Assert.Equal(0, empty.PaxBoarded);
        Assert.Null(empty.Since);
        Assert.Equal("Not called", empty.Detail);
    }

    // ---- flight sequence ----

    [Theory]
    [InlineData(FlightPhase.ColdAndDark, 0)]
    [InlineData(FlightPhase.Preflight, 0)]
    [InlineData(FlightPhase.Departure, 1)]
    [InlineData(FlightPhase.PushbackAndStart, 2)]
    [InlineData(FlightPhase.TakeoffRoll, 3)]
    [InlineData(FlightPhase.Cruise, 4)]
    [InlineData(FlightPhase.TaxiIn, 5)]
    [InlineData(FlightPhase.Shutdown, 6)]
    public void Sequence_LightsTheActiveBlock_AndMarksTheRest(FlightPhase phase, int active)
    {
        var steps = FlightStatusPresentation.Sequence(phase, false, false, null);

        Assert.Equal(FlightStatusPresentation.BlockLabels, steps.Select(s => s.Label));
        for (var i = 0; i < steps.Count; i++)
        {
            var expected = i < active ? "done" : i == active ? "active" : "pending";
            Assert.Equal(expected, steps[i].State);
            Assert.Equal(i < active ? "flightStatus.sequence.step.complete" : i == active ? "flightStatus.sequence.step.inProgress" : "flightStatus.sequence.step.pending",
                steps[i].SubTextId);
        }
    }

    [Fact]
    public void Sequence_ActiveDepartureStep_SaysWhereTheBoardingIs()
    {
        Assert.Equal("flightStatus.sequence.step.boarding", FlightStatusPresentation.Sequence(FlightPhase.Departure, true, false, GsxServiceStage.Active)[1].SubTextId);
        Assert.Equal("flightStatus.sequence.step.boardingComplete", FlightStatusPresentation.Sequence(FlightPhase.Departure, true, false, GsxServiceStage.Completed)[1].SubTextId);
        Assert.Equal("flightStatus.sequence.step.inProgress", FlightStatusPresentation.Sequence(FlightPhase.Departure, true, false, GsxServiceStage.Called)[1].SubTextId);
        Assert.Equal("flightStatus.sequence.step.inProgress", FlightStatusPresentation.Sequence(FlightPhase.Departure, true, false, null)[1].SubTextId);
        // Not on the Departure step: a boarding stage changes nothing.
        Assert.Equal("flightStatus.sequence.step.inProgress", FlightStatusPresentation.Sequence(FlightPhase.Preflight, false, false, GsxServiceStage.Active)[0].SubTextId);
    }

    [Fact]
    public void Engine_PillFollowsTheFreeze()
    {
        Assert.Equal(new EnginePill("tone-warn", "flightStatus.engine.frozen", "flightStatus.engine.frozenTitle"), FlightStatusPresentation.Engine(true));
        Assert.Equal(new EnginePill("tone-neutral", "flightStatus.engine.automatic", "flightStatus.engine.automaticTitle"), FlightStatusPresentation.Engine(false));
    }

    // ---- Sim card ----

    [Fact]
    public void SimRows_KeepTheProsim2GsxOrder_AndWords()
    {
        var session = new SimSessionSnapshot(SimSessionPhase.Walkaround, SimRunning: true, Paused: false, CameraState: 2, SimVersion: "MSFS 2024");

        var rows = FlightStatusPresentation.SimRows(session, simConnected: true, aircraftTitle: "A320 BAW");

        Assert.Equal(["running", "connected", "session", "paused", "walkaround", "cameraState", "version", "aircraft"], rows.Select(r => r.Id));
        Assert.Equal(["flightStatus.sim.running", "flightStatus.sim.connected", "flightStatus.sim.session", "flightStatus.sim.paused", "flightStatus.sim.walkaround",
            "flightStatus.sim.cameraState", "flightStatus.sim.version", "flightStatus.sim.aircraft"], rows.Select(r => r.LabelTextId));
        Assert.Equal([StatusRowKind.Indicator, StatusRowKind.Indicator, StatusRowKind.Indicator, StatusRowKind.Value, StatusRowKind.Value, StatusRowKind.Value, StatusRowKind.Value, StatusRowKind.Value],
            rows.Select(r => r.Kind));
        Assert.True(rows[0].Ok);
        Assert.Equal("OK", rows[0].Value.Resolve(T));
        Assert.True(rows[1].Ok);
        Assert.False(rows[2].Ok);                       // walkaround is not "in session"
        Assert.Equal("—", rows[2].Value.Resolve(T));
        Assert.Equal("No", rows[3].Value.Resolve(T));
        Assert.Equal("Yes", rows[4].Value.Resolve(T));
        Assert.Equal("2", rows[5].Value.Literal);
        Assert.Equal("MSFS 2024", rows[6].Value.Literal);
        Assert.Equal("A320 BAW", rows[7].Value.Literal);

        var empty = FlightStatusPresentation.SimRows(SimSessionSnapshot.Empty, false, null);
        Assert.All(empty.Skip(5), r => Assert.Equal("—", r.Value.Resolve(T)));
    }

    // ---- App card ----

    [Fact]
    public void AppRows_KeepTheOrder_AndReadOneProsimLinkThreeWays()
    {
        var links = new ConnectionFlags(SimConnected: true, ProsimConnected: true, GsxConnected: false);
        var data = new FlightDataSnapshot { IsValid = false, OnGround = true, AnyEngineRunning = false, GroundSpeedKt = 0.4 };

        var rows = FlightStatusPresentation.AppRows(links, automationEnabled: true, audioEnabled: false, "BAW A320", "A320", data, null, CabinSecureState.None);

        Assert.Equal(["gsxController", "prosimBinary", "aircraftInterface", "sdkConnected", "automationController", "audioController",
            "profile", "aircraft", "onGround", "enginesRunning", "inMotion", FlightStatusPresentation.AircraftStateRowId, "cabin"], rows.Select(r => r.Id));
        Assert.Equal([false, true, true, true, true, false], rows.Take(6).Select(r => r.Ok));
        Assert.Equal("BAW A320", rows[6].Value.Literal);
        Assert.Equal("True", rows[8].Value.Resolve(T));
        Assert.Equal("False", rows[9].Value.Resolve(T));
        Assert.Equal("False", rows[10].Value.Resolve(T));   // 0.4 kt is below the 0.5 kt motion threshold
        Assert.Equal(StatusRowKind.Pill, rows[11].Kind);
        Assert.Equal(StatusRowKind.Pill, rows[12].Kind);

        // The card reads the sample whether or not it is valid; no sample at all is the dash.
        var moving = FlightStatusPresentation.AppRows(links, true, true, null, null, data with { GroundSpeedKt = 0.6 }, null, CabinSecureState.None);
        Assert.Equal("True", moving[10].Value.Resolve(T));
        var noData = FlightStatusPresentation.AppRows(links, true, true, null, null, null, null, CabinSecureState.None);
        Assert.All(noData.Skip(6).Take(5), r => Assert.Equal("—", r.Value.Resolve(T)));
    }

    [Fact]
    public void AircraftStateRow_NotRunYet_IsTheNeutralDash()
    {
        var row = FlightStatusPresentation.AircraftStateRow(null);

        Assert.Equal(FlightStatusPresentation.AircraftStateRowId, row.Id);
        Assert.Equal("flightStatus.aircraftState.label", row.LabelTextId);
        Assert.Equal("tone-neutral", row.Tone);
        Assert.Equal("—", row.Value.Resolve(T));
        Assert.Null(row.Tooltip);
    }

    [Fact]
    public void AircraftStateRow_Pass_ListsUncheckedItemsOnTheTitle()
    {
        var clean = FlightStatusPresentation.AircraftStateRow(new AircraftStateCheckView(T0, AircraftStateCheckStatus.Pass, [], [], null, false));
        Assert.Equal("tone-ok", clean.Tone);
        Assert.Equal("flightStatus.aircraftState.pass", clean.Value.TextId);
        Assert.Null(clean.Tooltip);

        var partial = FlightStatusPresentation.AircraftStateRow(new AircraftStateCheckView(T0, AircraftStateCheckStatus.Pass, [], ["Battery 1 OFF", "APU master OFF"], null, false));
        Assert.Equal("flightStatus.aircraftState.unchecked", partial.Tooltip!.TextId);
        Assert.Equal("Battery 1 OFF, APU master OFF", partial.Tooltip.Arguments[0]);
    }

    [Fact]
    public void AircraftStateRow_Mismatch_CountsAndSpeaksThePhrases()
    {
        var one = FlightStatusPresentation.AircraftStateRow(new AircraftStateCheckView(T0, AircraftStateCheckStatus.Mismatch,
            [new AircraftStateMismatchView("Battery 1 OFF", "battery 1 is on")], [], null, true));
        Assert.Equal("tone-warn", one.Tone);
        Assert.Equal("flightStatus.aircraftState.mismatchOne", one.Value.TextId);
        Assert.Equal("1 mismatch", one.Value.Resolve(T));
        Assert.Equal("battery 1 is on", one.Tooltip!.Literal);

        var many = FlightStatusPresentation.AircraftStateRow(new AircraftStateCheckView(T0, AircraftStateCheckStatus.Mismatch,
            [new AircraftStateMismatchView("Battery 1 OFF", "battery 1 is on"), new AircraftStateMismatchView("Beacon OFF", "the beacon is on")], [], null, true));
        Assert.Equal("flightStatus.aircraftState.mismatchMany", many.Value.TextId);
        Assert.Equal("2 mismatches", many.Value.Resolve(T));
        Assert.Equal("battery 1 is on, the beacon is on", many.Tooltip!.Literal);
    }

    [Fact]
    public void AircraftStateRow_Skipped_SaysNotChecked_WithTheReason()
    {
        var row = FlightStatusPresentation.AircraftStateRow(new AircraftStateCheckView(T0, AircraftStateCheckStatus.Skipped, [], [], "turnaround", false));

        Assert.Equal("tone-neutral", row.Tone);
        Assert.Equal("flightStatus.aircraftState.notChecked", row.Value.TextId);
        Assert.Equal("turnaround", row.Tooltip!.Literal);
    }

    [Theory]
    [InlineData(CabinSecureState.None, "tone-neutral", null, "flightStatus.cabin.noneTitle")]
    [InlineData(CabinSecureState.Securing, "tone-warn", "flightStatus.cabin.securing", "flightStatus.cabin.securingTitle")]
    [InlineData(CabinSecureState.Secure, "tone-ok", "flightStatus.cabin.secure", "flightStatus.cabin.secureTitle")]
    public void CabinRow_FollowsTheSecuringWait(CabinSecureState state, string tone, string? textId, string titleId)
    {
        var row = FlightStatusPresentation.CabinRow(state);

        Assert.Equal("cabin", row.Id);
        Assert.Equal("flightStatus.cabin.label", row.LabelTextId);
        Assert.Equal(tone, row.Tone);
        Assert.Equal(textId, row.Value.TextId);
        if (textId is null)
        {
            Assert.Equal("—", row.Value.Literal);
        }

        Assert.Equal(titleId, row.Tooltip!.TextId);
    }

    // ---- GSX card ----

    [Fact]
    public void GsxRows_KeepTheOrder_AndTheReadinessIndicator()
    {
        var gsx = GsxDiagnosticsSnapshot.Empty with { Readiness = "Ready", StartupSid = "sid-1", GateRequest = "B12: Confirmed" };

        var rows = FlightStatusPresentation.GsxRows(gsx, gsxConnected: true, departureStarted: true, departureComplete: false);

        Assert.Equal(["running", "couatlVars", "droppedCall", "couatlStarted", "menuState", "paxTarget", "paxTotal", "cargo", "departureServices", "assignedGate", "lastHandlerEvent"],
            rows.Select(r => r.Id));
        Assert.True(rows[0].Ok);
        Assert.True(rows[1].Ok);
        Assert.Equal("sid-1", rows[3].Value.Literal);
        Assert.Equal("flightStatus.gsx.menuClosed", rows[4].Value.TextId);
        Assert.Equal("—", rows[5].Value.Resolve(T));
        Assert.Equal("—", rows[6].Value.Literal);
        Assert.Equal("—", rows[7].Value.Literal);
        Assert.Equal("flightStatus.gsx.departure.running", rows[8].Value.TextId);
        Assert.Equal("B12: Confirmed", rows[9].Value.Literal);
        Assert.Equal("—", rows[10].Value.Literal);

        Assert.False(FlightStatusPresentation.GsxRows(GsxDiagnosticsSnapshot.Empty, true, false, false)[1].Ok);   // "Disconnected" readiness
    }

    [Fact]
    public void GsxRows_MenuState_ShowsTheTitleOrTheWord()
    {
        Assert.Equal("Select handling", FlightStatusPresentation.GsxRows(GsxDiagnosticsSnapshot.Empty with { MenuShown = true, MenuTitle = "Select handling" }, true, false, false)[4].Value.Literal);
        Assert.Equal("flightStatus.gsx.menuShown", FlightStatusPresentation.GsxRows(GsxDiagnosticsSnapshot.Empty with { MenuShown = true, MenuTitle = null }, true, false, false)[4].Value.TextId);
    }

    [Fact]
    public void GsxRows_CountersAndHandlerEvent_KeepTheProsim2GsxShapes()
    {
        var gsx = GsxDiagnosticsSnapshot.Empty with
        {
            BoardingCounters = new GsxBoardingCountersView(150, 60, null, 40.4, null),
            LastHandlerEvent = new GsxHandlerEventView(T0, "Boarding", "Active"),
        };

        var rows = FlightStatusPresentation.GsxRows(gsx, true, false, false);

        Assert.Equal("150", rows[5].Value.Literal);
        Assert.Equal("60 | —", rows[6].Value.Literal);
        Assert.Equal("40% | —", rows[7].Value.Literal);
        Assert.Matches(@"^\d\d:\d\d:\d\d Boarding: Active$", rows[10].Value.Literal);
        Assert.Matches(@"^\d\d:\d\d:\d\d gateReset$", FlightStatusPresentation.HandlerEvent(new GsxHandlerEventView(T0, "gateReset", "")));
    }

    [Fact]
    public void DepartureSummary_ThreeWords()
    {
        Assert.Equal("flightStatus.gsx.departure.notStarted", FlightStatusPresentation.DepartureSummary(false, false).TextId);
        Assert.Equal("flightStatus.gsx.departure.running", FlightStatusPresentation.DepartureSummary(true, false).TextId);
        Assert.Equal("flightStatus.gsx.departure.complete", FlightStatusPresentation.DepartureSummary(true, true).TextId);
    }

    [Fact]
    public void DroppedCallRow_StandsUntilALaterCallConfirms()
    {
        var none = FlightStatusPresentation.DroppedCallRow(null);
        Assert.Equal("tone-neutral", none.Tone);
        Assert.Equal("—", none.Value.Literal);
        Assert.Equal("flightStatus.gsx.dropped.noneTitle", none.Tooltip!.TextId);

        var noMenu = FlightStatusPresentation.DroppedCallRow(new GsxDroppedCallView(T0, "Deboarding", null));
        Assert.Equal("tone-warn", noMenu.Tone);
        Assert.Equal("flightStatus.gsx.dropped.checkMenu", noMenu.Value.TextId);
        Assert.Equal("Deboarding", noMenu.Value.Arguments[0]);
        Assert.Matches(Clock, (string)noMenu.Value.Arguments[1]);
        Assert.Equal("flightStatus.gsx.dropped.title", noMenu.Tooltip!.TextId);

        var menuOpen = FlightStatusPresentation.DroppedCallRow(new GsxDroppedCallView(T0, "Deboarding", "Change parking"));
        Assert.Equal("flightStatus.gsx.dropped.menuOpen", menuOpen.Value.TextId);
        Assert.Equal("Change parking", menuOpen.Value.Arguments[2]);
    }

    // ---- Services card ----

    [Fact]
    public void ServiceRows_KeepTheOrder_IssuesThirtyOneAndFortyFive()
    {
        var rows = FlightStatusPresentation.ServiceRows(GsxDiagnosticsSnapshot.Empty, refuelSopMode: false, fuelConfirmed: false, departureComplete: false);

        Assert.Equal(["groundPrep", FlightStatusPresentation.RefuelRowId, "catering", "lavatory", "water", "cleaning", "gpu", "boarding", "deboarding", "pushback",
            "jetway", "jetwayConnected", "stairs", "stairsConnected"], rows.Select(r => r.Id));
        Assert.Equal(["flightStatus.services.groundPrep", "flightStatus.services.refuel", "flightStatus.services.catering", "flightStatus.services.lavatory",
            "flightStatus.services.water", "flightStatus.services.cleaning", "flightStatus.services.gpu", "flightStatus.services.boarding", "flightStatus.services.deboarding",
            "flightStatus.services.pushback", "flightStatus.services.jetway", "flightStatus.services.jetwayConnected", "flightStatus.services.stairs", "flightStatus.services.stairsConnected"],
            rows.Select(r => r.LabelTextId));
        Assert.All(rows, r => Assert.Equal(StatusRowKind.Pill, r.Kind));
        // Nothing mirrored: every pill is the neutral dash (a wrong id would look exactly like this forever — #31).
        Assert.All(rows, r => Assert.Equal("tone-neutral", r.Tone));
        Assert.All(rows, r => Assert.Equal("—", r.Value.Resolve(T)));
    }

    [Theory]
    [InlineData(GsxServiceStage.Waiting, "tone-neutral")]
    [InlineData(GsxServiceStage.Held, "tone-neutral")]
    [InlineData(GsxServiceStage.Skipped, "tone-neutral")]
    [InlineData(GsxServiceStage.Called, "tone-warn")]
    [InlineData(GsxServiceStage.Requested, "tone-warn")]
    [InlineData(GsxServiceStage.Active, "tone-active")]
    [InlineData(GsxServiceStage.Completed, "tone-ok")]
    public void ServiceRow_TonesTheLatchedStage(GsxServiceStage stage, string tone)
    {
        var row = FlightStatusPresentation.ServiceRow(Gsx(Service(GsxServiceIds.Catering, stage, "requested", "Requested")), "catering", "flightStatus.services.catering", GsxServiceIds.Catering);

        Assert.Equal(tone, row.Tone);
        Assert.Null(row.Tooltip);
        if (stage == GsxServiceStage.Completed)
        {
            Assert.Equal("flightStatus.services.completed", row.Value.TextId);
        }
        else
        {
            Assert.Equal("requested", row.Value.Literal);     // the raw semantic word for the in-between stages
        }
    }

    [Fact]
    public void ServiceRow_CompletedLatch_OutlivesTheMirrorFlippingBack()
    {
        // Issue #29: GSX flips a finished quick service back to "available"; the latched stage says completed.
        var row = FlightStatusPresentation.ServiceRow(Gsx(Service(GsxServiceIds.Catering, GsxServiceStage.Completed, "available", "Available")), "catering", "flightStatus.services.catering", GsxServiceIds.Catering);
        Assert.Equal("tone-ok", row.Tone);
        Assert.Equal("completed", row.Value.Resolve(T));

        // No semantic word: the mapped state shows. Ids match case-insensitively.
        var mapped = FlightStatusPresentation.ServiceRow(Gsx(Service("catering", GsxServiceStage.Active, null, "Running")), "catering", "flightStatus.services.catering", GsxServiceIds.Catering);
        Assert.Equal("Running", mapped.Value.Literal);
        Assert.Equal("tone-active", mapped.Tone);
    }

    [Fact]
    public void GroundPrepRow_TonesTheCoordinatorStage()
    {
        var none = FlightStatusPresentation.GroundPrepRow(null);
        Assert.Equal("tone-neutral", none.Tone);
        Assert.Equal("—", none.Value.Resolve(T));
        Assert.Null(none.Tooltip);
        Assert.Equal("groundPrep", none.Id);

        var complete = FlightStatusPresentation.GroundPrepRow(new GsxGroundPrepView("Complete", "jetway connected"));
        Assert.Equal("tone-ok", complete.Tone);
        Assert.Equal("flightStatus.services.prep.complete", complete.Value.TextId);
        Assert.Equal("Complete — jetway connected", complete.Tooltip!.Resolve(T));

        var holding = FlightStatusPresentation.GroundPrepRow(new GsxGroundPrepView("Settling", "holding: MSFS session not active"));
        Assert.Equal("tone-warn", holding.Tone);
        Assert.Equal("flightStatus.services.prep.holding", holding.Value.TextId);
        Assert.Equal("Settling — holding: MSFS session not active", holding.Tooltip!.Resolve(T));

        // Idle = parked chain (issue #98) — calm, not an activity and not a warning.
        var idle = FlightStatusPresentation.GroundPrepRow(new GsxGroundPrepView("Idle", "parked"));
        Assert.Equal("tone-neutral", idle.Tone);
        Assert.Equal("flightStatus.services.prep.idle", idle.Value.TextId);

        var reset = FlightStatusPresentation.GroundPrepRow(new GsxGroundPrepView("Reposition", "reset: session ended"));
        Assert.Equal("tone-warn", reset.Tone);
        Assert.Equal("flightStatus.services.prep.reposition", reset.Value.TextId);

        var active = FlightStatusPresentation.GroundPrepRow(new GsxGroundPrepView("AnchorGate", "selecting the gate"));
        Assert.Equal("tone-active", active.Tone);
        Assert.Equal("Anchoring gate", active.Value.Resolve(T));
    }

    [Theory]
    [InlineData("Idle", "flightStatus.services.prep.idle")]
    [InlineData("Reposition", "flightStatus.services.prep.reposition")]
    [InlineData("Settling", "flightStatus.services.prep.settling")]
    [InlineData("AnchorGate", "flightStatus.services.prep.anchorGate")]
    [InlineData("GroundEquipment", "flightStatus.services.prep.groundEquipment")]
    [InlineData("JetwayStairs", "flightStatus.services.prep.jetwayStairs")]
    [InlineData("Complete", "flightStatus.services.prep.complete")]
    public void PrepStageDisplay_TranslatesTheCodenames(string stage, string textId)
        => Assert.Equal(textId, FlightStatusPresentation.PrepStageDisplay(stage).TextId);

    [Fact]
    public void PrepStageDisplay_UnknownStage_PassesThrough()
    {
        var future = FlightStatusPresentation.PrepStageDisplay("Fuelling");
        Assert.Null(future.TextId);
        Assert.Equal("Fuelling", future.Literal);
    }

    [Fact]
    public void RefuelRow_SopMode_WaitsForTheCrew()
    {
        var gsx = Gsx(Service(GsxServiceIds.Refueling, GsxServiceStage.Waiting, "available", "Available"));

        var awaiting = FlightStatusPresentation.RefuelRow(gsx, refuelSopMode: true, fuelConfirmed: false, departureComplete: false);
        Assert.Equal(FlightStatusPresentation.RefuelRowId, awaiting.Id);
        Assert.Equal("flightStatus.services.refuel", awaiting.LabelTextId);
        Assert.Equal("tone-warn", awaiting.Tone);
        Assert.Equal("flightStatus.refuel.awaiting", awaiting.Value.TextId);
        Assert.Equal("flightStatus.refuel.awaitingTitle", awaiting.Tooltip!.TextId);

        // Confirmed, or the departure already complete, or not SOP mode: a plain service row.
        foreach (var row in new[]
        {
            FlightStatusPresentation.RefuelRow(gsx, true, true, false),
            FlightStatusPresentation.RefuelRow(gsx, true, false, true),
            FlightStatusPresentation.RefuelRow(gsx, false, false, false),
        })
        {
            Assert.Equal("tone-neutral", row.Tone);
            Assert.Equal("available", row.Value.Literal);
            Assert.Null(row.Tooltip);
        }

        var done = FlightStatusPresentation.RefuelRow(Gsx(Service(GsxServiceIds.Refueling, GsxServiceStage.Completed)), false, true, false);
        Assert.Equal("tone-ok", done.Tone);
        Assert.Equal("flightStatus.services.completed", done.Value.TextId);
    }

    [Fact]
    public void FuelConfirmed_PillOnlyOnceConfirmed()
    {
        Assert.Null(FlightStatusPresentation.FuelConfirmed(FuelConfirmationSnapshot.Empty));

        var pill = FlightStatusPresentation.FuelConfirmed(new FuelConfirmationSnapshot(true, 6500, T0, "voice"))!;
        Assert.Equal("flightStatus.refuel.confirmedKg", pill.Text.TextId);
        Assert.Equal("6500", pill.Text.Arguments[0]);
        Assert.Equal("6500 kg confirmed", pill.Text.Resolve(T));
        Assert.Equal("flightStatus.refuel.confirmedTitle", pill.Tooltip.TextId);
        Assert.Matches(Clock, (string)pill.Tooltip.Arguments[0]);
        Assert.Equal("voice", pill.Tooltip.Arguments[1]);
    }

    [Fact]
    public void GpuRow_TheDatarefBeatsTheMirror()
    {
        // Issue #33: GSX says "available" while the unit is physically connected.
        var mirror = Gsx(Service(GsxServiceIds.Gpu, GsxServiceStage.Waiting, "available", "Available"));

        var connected = FlightStatusPresentation.GpuRow(mirror with { GroundPowerConnected = true });
        Assert.Equal("gpu", connected.Id);
        Assert.Equal("flightStatus.services.gpu", connected.LabelTextId);
        Assert.Equal("tone-ok", connected.Tone);
        Assert.Equal("flightStatus.services.gpuConnected", connected.Value.TextId);

        var notConnected = FlightStatusPresentation.GpuRow(mirror with { GroundPowerConnected = false });
        Assert.Equal("tone-neutral", notConnected.Tone);
        Assert.Equal("available", notConnected.Value.Literal);

        var unknown = FlightStatusPresentation.GpuRow(mirror);
        Assert.Equal("available", unknown.Value.Literal);
    }

    [Theory]
    [InlineData(GsxServiceStage.Active, true)]
    [InlineData(GsxServiceStage.Completed, true)]
    [InlineData(GsxServiceStage.Waiting, false)]
    [InlineData(GsxServiceStage.Called, false)]
    [InlineData(GsxServiceStage.Requested, false)]
    public void ConnectedRow_DockedWhileTheOperateServiceIsActiveOrCompleted(GsxServiceStage stage, bool connected)
    {
        var row = FlightStatusPresentation.ConnectedRow(Gsx(Service(GsxServiceIds.OperateJetways, stage)), "jetwayConnected", "flightStatus.services.jetwayConnected", GsxServiceIds.OperateJetways);

        Assert.Equal(connected ? "tone-ok" : "tone-neutral", row.Tone);
        Assert.Equal(connected ? "Connected" : "—", row.Value.Resolve(T));

        var absent = FlightStatusPresentation.ConnectedRow(GsxDiagnosticsSnapshot.Empty, "jetwayConnected", "flightStatus.services.jetwayConnected", GsxServiceIds.OperateJetways);
        Assert.Equal("tone-neutral", absent.Tone);
    }

    // ---- tile order (commits c2739b5 / c1b2824 / cb14e97) ----

    [Fact]
    public void TileOrder_DefaultIsHeroSequenceSimAppGsxServices_AndADropSwapsTwoTiles()
    {
        Assert.Equal([FlightStatusTiles.Hero, FlightStatusTiles.Radios, FlightStatusTiles.Sequence, FlightStatusTiles.Sim, FlightStatusTiles.App, FlightStatusTiles.Gsx, FlightStatusTiles.Services],
            FlightStatusTiles.DefaultOrder);
        Assert.True(FlightStatusTiles.IsDefault([]));

        // A drop trades the two tiles' places and nothing else moves (owner decision 2026-10-07).
        Assert.Equal(["sim", "radios", "sequence", "hero", "app", "gsx", "services"], FlightStatusTiles.Swap(FlightStatusTiles.DefaultOrder, "sim", "hero"));
        // The arrows move one place.
        Assert.Equal(["radios", "hero", "sequence", "sim", "app", "gsx", "services"], FlightStatusTiles.Shift(FlightStatusTiles.DefaultOrder, "hero", +1));
        // A partial or stale saved list still shows every tile.
        Assert.Equal(["gsx", "hero", "radios", "sequence", "sim", "app", "services"], FlightStatusTiles.Normalize(["GSX", "unknown", "gsx"]));
    }

    [Fact]
    public void Resolve_Invariant_UnderAGermanCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var metrics = FlightStatusPresentation.Metrics(new FlightDataSnapshot { IsValid = true, GroundSpeedKt = 245.6, AltitudeFt = 12345.4, VerticalSpeedFpm = -1850.2 }, 12.3, null, kg => "");
            Assert.Equal("246", metrics[0].Figure);
            Assert.Equal("12,345", metrics[1].Figure);
            Assert.Equal("-1,850", metrics[2].Figure);
            Assert.Equal("012°", metrics[3].Figure);
            Assert.Equal("40", FlightStatusPresentation.Gate(new GateStatusSnapshot(GateState.Boarding, null, 60, 150, null, null, "", null, false)).FillWidth);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
