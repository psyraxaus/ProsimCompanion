using System.Globalization;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Boarding;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.Radios;
using ProsimCompanion.Core.State;
using ProsimCompanion.Core.Weather;

namespace ProsimCompanion.Web.Components;

/// <summary>Which subsystems the Flight Status page counts as connected, read once from the
/// connection snapshot so every row that says "connected" means the same thing.</summary>
public sealed record ConnectionFlags(bool SimConnected, bool ProsimConnected, bool GsxConnected);

/// <summary>One figure of the hero's metrics strip: the label, the formatted figure and the
/// unit suffix (null for the fuel figure, whose unit is inside the text), plus whether the
/// figure glows (the live ones: speed, vertical speed, heading).</summary>
public sealed record HeroMetric(string LabelTextId, string Figure, string? Unit, bool Glow);

/// <summary>One item of the hero's distance / ETA row (issue #145). <paramref name="Tag"/>
/// names where the figure came from ("GS", "PLAN", "EST") and is null when there is no figure.</summary>
public sealed record HeroProgressItem(string LabelTextId, string Figure, string? Tag);

/// <summary>One step of the flight sequence strip: its caps label, its CSS state
/// ("done" / "active" / "pending") and the text id of the line under it.</summary>
public sealed record SequenceStep(string Label, string State, string SubTextId);

/// <summary>The phase-engine pill beside the Force controls: frozen or automatic.</summary>
public sealed record EnginePill(string Tone, string TextId, string TitleTextId);

/// <summary>Everything one hero weather card draws (owner sign-off 2026-09-22, canvas
/// "Weather Card Title Line" v3): the title line, the sky graphic, and either the two data
/// lines or the missing-observation line. Figures come pre-formatted so the markup never
/// formats a METAR itself.</summary>
/// <param name="TitleTextId">"Local weather" / "Weather at destination" / "Weather at alternate".</param>
/// <param name="Name">The airport name, else the ICAO, else "No flight plan".</param>
/// <param name="Icao">The station, for the hover title of the name.</param>
/// <param name="Atis">The ATIS letter chip, or null for none.</param>
/// <param name="SkyClass">The <c>sky-*</c> class of the sky graphic.</param>
/// <param name="SkyIcon">The icon name of the sky graphic.</param>
/// <param name="SkyLabel">The caps word under the icon.</param>
/// <param name="RawTitle">The raw METAR (or the failure line) on the hover title; null for none.</param>
/// <param name="Found">True when the two data lines show; false shows <paramref name="Missing"/>.</param>
/// <param name="WindArrowDeg">The wind arrow's rotation, or null for no arrow (calm / unknown direction).</param>
/// <param name="Gust">The gust figure, or null for none.</param>
/// <param name="Stamp">"METAR 10:20Z" / "METAR —".</param>
/// <param name="Missing">The line shown instead of the data when no observation was found.</param>
public sealed record WeatherCardView(
    string TitleTextId,
    DisplayText Name,
    string Icao,
    string? Atis,
    string SkyClass,
    string SkyIcon,
    string SkyLabel,
    string? RawTitle,
    bool Found,
    int? WindArrowDeg,
    string Wind,
    int? Gust,
    string Visibility,
    string Ceiling,
    string Temperature,
    string Qnh,
    string Stamp,
    DisplayText? Missing);

/// <summary>Everything the gate monitor strip draws (owner spec 2026-09-22): one of five
/// forward-only states, the same size in every state, dimmed from taxi-out onward.</summary>
/// <param name="StateClass">The state's CSS class ("open", "boarding", "final-call", "closed-after", "closed").</param>
/// <param name="Dimmed">True from taxi-out onward: the strip fades but never hides.</param>
/// <param name="Detail">The short line under the gate id ("Not called", "Jetway · door 1L").</param>
/// <param name="Since">"· since 10:41Z" for the hover title, or null when the state has no stamp.</param>
/// <param name="GateId">The confirmed gate or the dash.</param>
/// <param name="StateTextId">The pack id of the big state word.</param>
/// <param name="BoardingDot">True while boarding: the state shows the live dot instead of an icon.</param>
/// <param name="Icon">The state icon when <paramref name="BoardingDot"/> is false.</param>
/// <param name="PaxTarget">The planned total, or null while GSX has no pax session (shows "— PAX").</param>
/// <param name="PaxBoarded">The boarded count (0 until GSX reports).</param>
/// <param name="PaxGlow">True while boarding: the boarded figure glows.</param>
/// <param name="Std">"STD −12 MIN" / "STD +3 MIN" / "STD 10:45Z" / "STD —".</param>
/// <param name="StdSoon">True on final call: the STD figure takes the "soon" tone.</param>
/// <param name="ProgressPercent">The boarded share for the progress bar's ARIA value.</param>
/// <param name="FillWidth">The bar fill width, invariant "42" (percent) for the inline style.</param>
public sealed record GateStripView(
    string StateClass,
    bool Dimmed,
    string Detail,
    DisplayText? Since,
    string GateId,
    string StateTextId,
    bool BoardingDot,
    string Icon,
    int? PaxTarget,
    int PaxBoarded,
    bool PaxGlow,
    string Std,
    bool StdSoon,
    int ProgressPercent,
    string FillWidth);

/// <summary>One tap-to-tune station on the Radios tile (owner request 2026-10-25).</summary>
/// <param name="Type">SayIntentions' station type ("GROUND").</param>
/// <param name="Label">The callsign when known, else the type as a word ("Ground").</param>
/// <param name="IsActive">COM1 active is on this frequency.</param>
/// <param name="IsAssigned">This is the frequency ATC expects.</param>
public sealed record RadioStationChip(string Type, string Label, int Khz, string Frequency, bool IsActive, bool IsAssigned);

/// <summary>The Radios tile: COM1 against what SayIntentions ATC expects, plus the local
/// airport's stations. <see cref="StateClass"/> is one of off / unknown / match / mismatch.</summary>
public sealed record RadiosTileView(
    string Active,
    string Standby,
    bool Com1Live,
    string? AssignedStation,
    string? AssignedFrequency,
    int AssignedKhz,
    DisplayText? AssignedSince,
    string StateClass,
    string StateTextId,
    string? HintTextId,
    bool CanTuneAssigned,
    string StationsIcao,
    IReadOnlyList<RadioStationChip> Stations,
    string? LastAtcLine,
    DisplayText? LastAtcSince);

/// <summary>The refuel row's confirmation pill (2026-09-19 SOP): the confirmed figure and
/// the "when / by whom" hover title, present only once the crew has confirmed.</summary>
public sealed record FuelConfirmedPill(DisplayText Text, DisplayText Tooltip);

/// <summary>
/// The Flight Status page's presentation module: store snapshots and options in, row and
/// label records out (ADR-0001 web-first dashboard, Prosim2GSX FlightStatusPanel clone). The
/// page's markup renders the records and owns only the circuit plumbing (watches, timers,
/// buttons); every tone, text id, order and visibility rule lives here where xunit reaches it.
/// The phase mapping (<see cref="ActiveBlock"/>, <see cref="PhaseDisplay"/>) is shared with
/// the change log (<see cref="FlightStatusChangeLog"/>, issue #110) and the pop-out board so
/// no surface can disagree about what the pilot was shown. Second adapter of the same seam:
/// <see cref="GsxPresentation"/> for the GSX status panel.
/// </summary>
public static class FlightStatusPresentation
{
    /// <summary>Row ids the markup hangs controls on.</summary>
    public const string AircraftStateRowId = "aircraftState";

    /// <summary>The refuel row carries the fuel-confirm button and the confirmation pill.</summary>
    public const string RefuelRowId = "refuel";

    /// <summary>Progress-strip block labels, left to right (Prosim2GSX FlightStatusPanel clone).</summary>
    public static readonly string[] BlockLabels =
        ["PREFLIGHT", "DEPARTURE", "PUSHBACK", "TAXI OUT", "FLIGHT", "TAXI IN", "ARRIVAL"];

    /// <summary>Index of the highlighted block — a pure phase view since 2026-09-13 (issue
    /// #110, owner's Option 2). Until then the strip carried a DEPARTURE block that lit from
    /// the GSX departure sequence while the phase text still said PREFLIGHT: on the
    /// 2026-09-13 leg the two disagreed for 32 minutes (10:43–11:15), which the owner read as
    /// the title "dropping back" (#130). The DEPARTURE block returned on 2026-09-20 as a
    /// real engine phase (boarding underway → pushback), so the strip and the text still
    /// come from the one state machine. The departure flags stay in the signature so the
    /// change log keeps recording them next to the phase.</summary>
    public static int ActiveBlock(FlightPhase phase, bool departureStarted, bool departureComplete) => phase switch
    {
        FlightPhase.Unknown or FlightPhase.ColdAndDark or FlightPhase.Preflight => 0,
        FlightPhase.Departure => 1,
        FlightPhase.PushbackAndStart => 2,
        FlightPhase.TaxiOut or FlightPhase.TakeoffRoll => 3,
        FlightPhase.InitialClimb or FlightPhase.Climb or FlightPhase.Cruise
            or FlightPhase.Descent or FlightPhase.Approach => 4,
        FlightPhase.LandingRollout or FlightPhase.TaxiIn => 5,
        FlightPhase.Shutdown => 6,
        _ => 0,
    };

    /// <summary>The label of the highlighted block for the given inputs.</summary>
    public static string ActiveBlockLabel(FlightPhase phase, bool departureStarted, bool departureComplete)
        => BlockLabels[ActiveBlock(phase, departureStarted, departureComplete)];

    /// <summary>"PushbackAndStart" renders as "PUSHBACK AND START" — the phase text exactly
    /// as the Flight Phase card displays it.</summary>
    public static string PhaseDisplay(FlightPhase phase)
    {
        var pascal = phase.ToString();
        var result = new System.Text.StringBuilder(pascal.Length + 4);
        foreach (var ch in pascal)
        {
            if (char.IsUpper(ch) && result.Length > 0)
            {
                result.Append(' ');
            }
            result.Append(char.ToUpperInvariant(ch));
        }
        return result.ToString();
    }

    /// <summary>The rule's reason — the one-look answer to "why does it think we are on
    /// approach?" (review 2026-08-29); the dash before the first commit.</summary>
    public static string PhaseReason(FlightStateView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return view.LastTransitionReason is { Length: > 0 } reason ? reason : "—";
    }

    /// <summary>The last sample when it may be shown; null hides every live figure.</summary>
    public static FlightDataSnapshot? ValidData(FlightStateView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return view.Data is { IsValid: true } data ? data : null;
    }

    /// <summary>Which subsystems are connected, from the connection store's snapshot.</summary>
    public static ConnectionFlags Connections(IReadOnlyList<KeyValuePair<string, ConnectionState>> connections)
    {
        ArgumentNullException.ThrowIfNull(connections);
        return new ConnectionFlags(
            StateOf(Subsystems.SimConnect) == ConnectionState.Connected,
            StateOf(Subsystems.Prosim) == ConnectionState.Connected,
            StateOf(Subsystems.Gsx) == ConnectionState.Connected);

        ConnectionState StateOf(string subsystem)
            => connections.FirstOrDefault(pair => pair.Key == subsystem).Value;
    }

    // ── Hero card (ADR-0011) ──────────────────────────────────────────────────────────

    /// <summary>The route end: the OFP's ICAO or the "----" placeholder that keeps the shape.</summary>
    public static string RouteEnd(string? icao) => icao is { Length: > 0 } ? icao : "----";

    /// <summary>The line under the route: callsign (else ident), flight number, cruise level
    /// and STD, joined with " | "; or the "no flight plan" hint.</summary>
    public static DisplayText FlightLine(OfpData? ofp)
    {
        if (ofp is null)
        {
            return DisplayText.Id("flightStatus.hero.noFlightPlan");
        }

        return DisplayText.Raw(string.Join(" | ", new[]
        {
            ofp.Callsign.Length > 0 ? ofp.Callsign : ofp.Ident,
            ofp.FlightNumber.Length > 0 ? $"FLT {ofp.FlightNumber}" : null,
            ofp.CruiseFlightLevel > 0 ? $"CRZ FL{ofp.CruiseFlightLevel:D3}" : null,
            ofp.ScheduledOutUtc is { } std ? $"STD {std:HH:mm}Z" : null,
        }.Where(part => !string.IsNullOrEmpty(part))));
    }

    /// <summary>The five hero metrics in strip order. Ground speed, altitude and vertical
    /// speed come from the phase engine's last sample; heading and fuel on board are the two
    /// datarefs the sample does not carry, shown only while ProSim is connected (a stale
    /// cached value would otherwise outlive the connection).</summary>
    /// <param name="data">The valid sample (<see cref="ValidData"/>), or null.</param>
    /// <param name="headingDeg">The raw magnetic heading, or null while unreported / disconnected.</param>
    /// <param name="fuelOnBoardKg">The raw fuel total, or null while unreported / disconnected.</param>
    /// <param name="formatFuel">The display-unit formatter (kg or lb per the pilot's setting).</param>
    public static IReadOnlyList<HeroMetric> Metrics(FlightDataSnapshot? data, double? headingDeg, double? fuelOnBoardKg, Func<double, string> formatFuel)
    {
        ArgumentNullException.ThrowIfNull(formatFuel);
        var verticalSpeed = data is { } sample
            ? sample.VerticalSpeedFpm.ToString("+#,0;-#,0;0", CultureInfo.InvariantCulture)
            : "—";
        var heading = headingDeg is { } hdg ? $"{((hdg % 360 + 360) % 360):000}°" : "—";
        var fuel = fuelOnBoardKg is { } kg ? formatFuel(kg) : "—";
        return
        [
            new HeroMetric("flightStatus.hero.groundSpeed", Metric(data?.GroundSpeedKt, "F0"), "KTS", Glow: true),
            new HeroMetric("flightStatus.hero.altitude", Metric(data?.AltitudeFt, "N0"), "FT", Glow: false),
            new HeroMetric("flightStatus.hero.verticalSpeed", verticalSpeed, "FPM", Glow: true),
            new HeroMetric("flightStatus.hero.heading", heading, "MAG", Glow: true),
            new HeroMetric("flightStatus.hero.fuelOnBoard", fuel, null, Glow: false),
        ];
    }

    private static string Metric(double? value, string format)
        => value is { } v ? v.ToString(format, CultureInfo.InvariantCulture) : "—";

    /// <summary>The distance / ETA row (issue #145). Distances are great-circle DIRECT, never
    /// along the route; the tags name where each figure came from (GS = position and ground
    /// speed, PLAN = the time-based fallback, EST = the 3:1 top-of-descent estimate).</summary>
    public static IReadOnlyList<HeroProgressItem> Progress(FlightProgressSnapshot progress, Core.Fuel.HoldingEstimate? holding = null)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var items = new List<HeroProgressItem>
        {
            new("flightStatus.hero.toGo", FlightMonitorPresentation.Distance(progress.DistanceToGoNm), null),
            new("flightStatus.hero.flown", FlightMonitorPresentation.Distance(progress.DistanceFlownNm), null),
            new("flightStatus.hero.eta", FlightMonitorPresentation.Clock(progress.EtaUtc),
                progress.EtaUtc is null ? null : FlightMonitorPresentation.BasisTag(progress.EtaBasis)),
            new("flightStatus.hero.topOfDescent", FlightMonitorPresentation.MinutesToTod(progress.MinutesToTod),
                progress.MinutesToTod is null ? null : "EST"),
        };

        // Holding time (issue #183): only once a fuel check has produced one this flight.
        if (holding is not null)
        {
            items.Add(new HeroProgressItem("flightStatus.hero.holding", Core.Fuel.HoldingTimeCalculator.Figure(holding), holding.DivertNow ? null : "EST"));
        }

        return items;
    }

    // ── Hero widgets (owner sign-off 2026-09-22) ──────────────────────────────────────
    // Weather cards: the label on its own line, the airport name under it, the ATIS chip
    // top-right, then the sky graphic + condition word beside two mono data lines. The raw
    // METAR rides on the hover title. Values are formatted here only — classification and
    // the observation stamp come pre-computed from the store; the figure texts live in
    // WeatherCardFormat, shared with the pop-out Flight Monitor board.

    public static WeatherCardView Weather(WeatherCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        var facts = card.Facts;
        var found = card.Status == WxProbeStatus.Found;
        return new WeatherCardView(
            TitleTextId: card.Role switch
            {
                WeatherCardRole.Local => "flightStatus.weather.local",
                WeatherCardRole.Alternate => "flightStatus.weather.alternate",
                _ => "flightStatus.weather.destination",
            },
            Name: card.AirportName.Length > 0 ? DisplayText.Raw(card.AirportName)
                : card.Icao.Length > 0 ? DisplayText.Raw(card.Icao)
                : DisplayText.Id("flightStatus.weather.noFlightPlan"),
            Icao: card.Icao,
            Atis: facts.AtisLetter is { Length: > 0 } atis ? atis : null,
            SkyClass: "sky-" + card.Sky.ToString().ToLowerInvariant(),
            SkyIcon: WeatherCardFormat.SkyIcon(card.Sky),
            SkyLabel: card.SkyLabel,
            RawTitle: facts.RawMetar ?? card.Failure,
            Found: found,
            WindArrowDeg: facts.WindDirDeg is { } dir && facts.WindSpeedKt is > 0 ? dir : null,
            Wind: WeatherCardFormat.Wind(facts),
            Gust: facts.WindGustKt,
            Visibility: WeatherCardFormat.Visibility(facts),
            Ceiling: WeatherCardFormat.Ceiling(facts),
            Temperature: WeatherCardFormat.Temperature(facts),
            Qnh: WeatherCardFormat.Qnh(facts),
            Stamp: $"METAR {card.ObservedAt ?? "—"}",
            Missing: found ? null
                : card.Icao.Length == 0 ? DisplayText.Id("flightStatus.weather.fetchOfp")
                : DisplayText.RawOrNull(card.Failure) ?? DisplayText.Id("flightStatus.weather.noMetar"));
    }

    /// <summary>Gate monitor strip: five forward-only states (owner spec 2026-09-22), same
    /// size in every state; dims from taxi-out onward instead of disappearing.</summary>
    public static GateStripView Gate(GateStatusSnapshot gate)
    {
        ArgumentNullException.ThrowIfNull(gate);
        var stateClass = gate.State switch
        {
            GateState.Open => "open",
            GateState.Boarding => "boarding",
            GateState.FinalCall => "final-call",
            GateState.ClosedAfterBoarding => "closed-after",
            _ => "closed",
        };
        var stateTextId = gate.State switch
        {
            GateState.Open => "flightStatus.gate.open",
            GateState.Boarding => "flightStatus.gate.boarding",
            GateState.FinalCall => "flightStatus.gate.finalCall",
            _ => "flightStatus.gate.closed",
        };
        var icon = gate.State switch
        {
            GateState.Open => "door-open",
            GateState.FinalCall => "bell-ring",
            GateState.ClosedAfterBoarding => "circle-check",
            _ => "door-closed",
        };
        var std = gate switch
        {
            { StdUtc: null } => "STD —",
            { Dimmed: true } or { State: GateState.ClosedAfterBoarding } => $"STD {gate.StdUtc:HH:mm}Z",
            { MinutesToStd: { } m } when m >= 0 => $"STD −{m} MIN",
            { MinutesToStd: { } m } => $"STD +{-m} MIN",
            _ => "STD —",
        };
        return new GateStripView(
            StateClass: stateClass,
            Dimmed: gate.Dimmed,
            Detail: gate.Detail,
            Since: gate.ChangedAtUtc is { } at ? DisplayText.Id("flightStatus.gate.since", $"{at:HH:mm}Z") : null,
            GateId: gate.GateId ?? "—",
            StateTextId: stateTextId,
            BoardingDot: gate.State == GateState.Boarding,
            Icon: icon,
            PaxTarget: gate.PaxTarget,
            PaxBoarded: gate.PaxBoarded ?? 0,
            PaxGlow: gate.State == GateState.Boarding,
            Std: std,
            StdSoon: gate.State == GateState.FinalCall,
            ProgressPercent: (int)Math.Round(gate.Progress * 100),
            FillWidth: (gate.Progress * 100).ToString("F0", CultureInfo.InvariantCulture));
    }

    // ── Flight sequence ───────────────────────────────────────────────────────────────

    /// <summary>The seven sequence steps with their state and sub-line. The active DEPARTURE
    /// step (2026-09-20) says where the boarding is, since that is the evidence the phase
    /// engine entered it on.</summary>
    /// <param name="boardingStage">The latched GSX Boarding stage, or null when the mirror has no row.</param>
    public static IReadOnlyList<SequenceStep> Sequence(FlightPhase phase, bool departureStarted, bool departureComplete, GsxServiceStage? boardingStage)
    {
        var active = ActiveBlock(phase, departureStarted, departureComplete);
        var steps = new SequenceStep[BlockLabels.Length];
        for (var i = 0; i < steps.Length; i++)
        {
            var state = i < active ? "done" : i == active ? "active" : "pending";
            var subText = state == "active" && phase == FlightPhase.Departure
                ? boardingStage switch
                {
                    GsxServiceStage.Completed => "flightStatus.sequence.step.boardingComplete",
                    GsxServiceStage.Active => "flightStatus.sequence.step.boarding",
                    _ => "flightStatus.sequence.step.inProgress",
                }
                : state switch
                {
                    "done" => "flightStatus.sequence.step.complete",
                    "active" => "flightStatus.sequence.step.inProgress",
                    _ => "flightStatus.sequence.step.pending",
                };
            steps[i] = new SequenceStep(BlockLabels[i], state, subText);
        }

        return steps;
    }

    /// <summary>The phase-engine pill: "frozen" (warn) while a manual override holds the
    /// phase, "automatic" (neutral) while the rule table drives it.</summary>
    public static EnginePill Engine(bool frozen) => frozen
        ? new EnginePill("tone-warn", "flightStatus.engine.frozen", "flightStatus.engine.frozenTitle")
        : new EnginePill("tone-neutral", "flightStatus.engine.automatic", "flightStatus.engine.automaticTitle");

    // ── Sim / App / GSX / Services cards (Prosim2GSX FlightStatusPanel rows) ──────────

    /// <summary>"Yes"/"No" cells of the Sim card, in the page's language.</summary>
    public static DisplayText YesNo(bool value) => DisplayText.Id(value ? "flightStatus.common.yes" : "flightStatus.common.no");

    /// <summary>"True"/"False" cells of the App card; null stays the neutral dash.</summary>
    public static DisplayText TrueFalse(bool? value) => value switch
    {
        true => DisplayText.Id("flightStatus.common.true"),
        false => DisplayText.Id("flightStatus.common.false"),
        null => DisplayText.Dash,
    };

    /// <summary>The Sim card, top to bottom.</summary>
    public static IReadOnlyList<StatusRow> SimRows(SimSessionSnapshot session, bool simConnected, string? aircraftTitle)
    {
        ArgumentNullException.ThrowIfNull(session);
        return
        [
            StatusRow.Indicator("running", "flightStatus.sim.running", session.SimRunning),
            StatusRow.Indicator("connected", "flightStatus.sim.connected", simConnected),
            StatusRow.Indicator("session", "flightStatus.sim.session", session.Phase == SimSessionPhase.InSession),
            StatusRow.Mono("paused", "flightStatus.sim.paused", YesNo(session.Paused)),
            StatusRow.Mono("walkaround", "flightStatus.sim.walkaround", YesNo(session.Phase == SimSessionPhase.Walkaround)),
            StatusRow.Mono("cameraState", "flightStatus.sim.cameraState", session.CameraState?.ToString(CultureInfo.InvariantCulture)),
            StatusRow.Mono("version", "flightStatus.sim.version", session.SimVersion),
            StatusRow.Mono("aircraft", "flightStatus.sim.aircraft", aircraftTitle),
        ];
    }

    /// <summary>The App card, top to bottom. The three ProSim rows are one connection read
    /// three ways (Prosim2GSX showed binary / interface / SDK separately; this app has one
    /// link). <paramref name="data"/> is the engine's last sample as it is — not gated on
    /// validity, the way the card has always read it.</summary>
    public static IReadOnlyList<StatusRow> AppRows(
        ConnectionFlags links,
        bool automationEnabled,
        bool audioEnabled,
        string? profileName,
        string? aircraftTitle,
        FlightDataSnapshot? data,
        AircraftStateCheckView? check,
        CabinSecureState cabin)
    {
        ArgumentNullException.ThrowIfNull(links);
        return
        [
            StatusRow.Indicator("gsxController", "flightStatus.app.gsxController", links.GsxConnected),
            StatusRow.Indicator("prosimBinary", "flightStatus.app.prosimBinary", links.ProsimConnected),
            StatusRow.Indicator("aircraftInterface", "flightStatus.app.aircraftInterface", links.ProsimConnected),
            StatusRow.Indicator("sdkConnected", "flightStatus.app.sdkConnected", links.ProsimConnected),
            StatusRow.Indicator("automationController", "flightStatus.app.automationController", automationEnabled),
            StatusRow.Indicator("audioController", "flightStatus.app.audioController", audioEnabled),
            StatusRow.Mono("profile", "flightStatus.app.profile", profileName),
            StatusRow.Mono("aircraft", "flightStatus.app.aircraft", aircraftTitle),
            StatusRow.Mono("onGround", "flightStatus.app.onGround", TrueFalse(data?.OnGround)),
            StatusRow.Mono("enginesRunning", "flightStatus.app.enginesRunning", TrueFalse(data?.AnyEngineRunning)),
            StatusRow.Mono("inMotion", "flightStatus.app.inMotion", TrueFalse(data is { } motion ? motion.GroundSpeedKt > 0.5 : null)),
            AircraftStateRow(check),
            CabinRow(cabin),
        ];
    }

    /// <summary>The GSX card, top to bottom (the live GSX menu card follows it in the markup).</summary>
    public static IReadOnlyList<StatusRow> GsxRows(GsxDiagnosticsSnapshot gsx, bool gsxConnected, bool departureStarted, bool departureComplete)
    {
        ArgumentNullException.ThrowIfNull(gsx);
        return
        [
            StatusRow.Indicator("running", "flightStatus.gsx.running", gsxConnected),
            StatusRow.Indicator("couatlVars", "flightStatus.gsx.couatlVars", gsx.Readiness == "Ready"),
            DroppedCallRow(gsx.DroppedCall),
            StatusRow.Mono("couatlStarted", "flightStatus.gsx.couatlStarted", gsx.StartupSid),
            StatusRow.Mono("menuState", "flightStatus.gsx.menuState", gsx.MenuShown
                ? DisplayText.RawOrNull(gsx.MenuTitle) ?? DisplayText.Id("flightStatus.gsx.menuShown")
                : DisplayText.Id("flightStatus.gsx.menuClosed")),
            StatusRow.Mono("paxTarget", "flightStatus.gsx.paxTarget", gsx.BoardingCounters?.PaxTarget?.ToString(CultureInfo.InvariantCulture)),
            StatusRow.Mono("paxTotal", "flightStatus.gsx.paxTotal", PaxTotals(gsx.BoardingCounters)),
            StatusRow.Mono("cargo", "flightStatus.gsx.cargo", CargoTotals(gsx.BoardingCounters)),
            StatusRow.Mono("departureServices", "flightStatus.gsx.departureServices", DepartureSummary(departureStarted, departureComplete)),
            StatusRow.Mono("assignedGate", "flightStatus.gsx.assignedGate", gsx.GateRequest),
            StatusRow.Mono("lastHandlerEvent", "flightStatus.gsx.lastHandlerEvent", HandlerEvent(gsx.LastHandlerEvent)),
        ];
    }

    /// <summary>Prosim2GSX row shape: "boarded | deboarded" pax.</summary>
    public static string PaxTotals(GsxBoardingCountersView? counters) => counters is { } pax
        ? $"{pax.PaxBoarded?.ToString(CultureInfo.InvariantCulture) ?? "—"} | {pax.PaxDeboarded?.ToString(CultureInfo.InvariantCulture) ?? "—"}"
        : "—";

    /// <summary>Prosim2GSX row shape: "board% | deboard%" cargo.</summary>
    public static string CargoTotals(GsxBoardingCountersView? counters) => counters is { } cargo
        ? $"{FormatPercent(cargo.CargoBoardedPercent)} | {FormatPercent(cargo.CargoDeboardedPercent)}"
        : "—";

    private static string FormatPercent(double? value)
        => value is { } pct ? $"{pct:F0}%" : "—";

    /// <summary>Two producers share this row: service lifecycle edges ("Boarding: Active")
    /// and the in-sim handler script's events ("boardingRequested", "gateReset: user_changed").</summary>
    public static string HandlerEvent(GsxHandlerEventView? handlerEvent) => handlerEvent is { } evt
        ? $"{evt.Timestamp.ToLocalTime():HH:mm:ss} {evt.Service}{(evt.Event.Length > 0 ? $": {evt.Event}" : "")}"
        : "—";

    public static DisplayText DepartureSummary(bool started, bool complete) => DisplayText.Id(
        complete ? "flightStatus.gsx.departure.complete"
        : started ? "flightStatus.gsx.departure.running"
        : "flightStatus.gsx.departure.notStarted");

    /// <summary>The Services card, top to bottom. Service ids MUST be the canonical
    /// <see cref="GsxServiceIds"/> strings — the mirror never carries "Jetway"/"Stairs"/
    /// "Pushback", so home-grown names render "—" forever (issue #31). Reposition has no
    /// Remote API service (menu-driven), so its row is fed by the ground-prep coordinator's
    /// published stage instead (issue #45).</summary>
    public static IReadOnlyList<StatusRow> ServiceRows(GsxDiagnosticsSnapshot gsx, bool refuelSopMode, bool fuelConfirmed, bool departureComplete)
    {
        ArgumentNullException.ThrowIfNull(gsx);
        return
        [
            GroundPrepRow(gsx.GroundPrep),
            RefuelRow(gsx, refuelSopMode, fuelConfirmed, departureComplete),
            ServiceRow(gsx, "catering", "flightStatus.services.catering", GsxServiceIds.Catering),
            ServiceRow(gsx, "lavatory", "flightStatus.services.lavatory", GsxServiceIds.Lavatory),
            ServiceRow(gsx, "water", "flightStatus.services.water", GsxServiceIds.Water),
            ServiceRow(gsx, "cleaning", "flightStatus.services.cleaning", GsxServiceIds.Cleaning),
            GpuRow(gsx),
            ServiceRow(gsx, "boarding", "flightStatus.services.boarding", GsxServiceIds.Boarding),
            ServiceRow(gsx, "deboarding", "flightStatus.services.deboarding", GsxServiceIds.Deboarding),
            ServiceRow(gsx, "pushback", "flightStatus.services.pushback", GsxServiceIds.Departure),
            ServiceRow(gsx, "jetway", "flightStatus.services.jetway", GsxServiceIds.OperateJetways),
            ConnectedRow(gsx, "jetwayConnected", "flightStatus.services.jetwayConnected", GsxServiceIds.OperateJetways),
            ServiceRow(gsx, "stairs", "flightStatus.services.stairs", GsxServiceIds.OperateStairs),
            ConnectedRow(gsx, "stairsConnected", "flightStatus.services.stairsConnected", GsxServiceIds.OperateStairs),
        ];
    }

    /// <summary>The mirror's row for a canonical service id, or null when it carries none.</summary>
    public static GsxServiceView? ServiceView(GsxDiagnosticsSnapshot gsx, string serviceId)
    {
        ArgumentNullException.ThrowIfNull(gsx);
        return gsx.Services.FirstOrDefault(service =>
            string.Equals(service.Id, serviceId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A plain service row. Tone and text come from the LATCHED stage, not the raw
    /// mirror: GSX flips finished quick services (and refuel) back to "available", which read
    /// as never-run while the departure board said Completed (issue #29). The raw semantic
    /// text still shows for the in-between stages.</summary>
    public static StatusRow ServiceRow(GsxDiagnosticsSnapshot gsx, string id, string labelTextId, string serviceId)
    {
        var view = ServiceView(gsx, serviceId);
        return StatusRow.Pill(id, labelTextId, ServiceTone(view), ServiceText(view));
    }

    /// <summary>The pill tone of a latched service stage (the card's own scale; the GSX
    /// status board's lamps use <see cref="GsxPresentation.StageLamp"/>).</summary>
    public static string ServiceTone(GsxServiceView? view) => view?.Stage switch
    {
        GsxServiceStage.Completed => "tone-ok",
        GsxServiceStage.Active => "tone-active",
        GsxServiceStage.Requested or GsxServiceStage.Called => "tone-warn",
        _ => "tone-neutral",
    };

    /// <summary>"completed" once latched; the raw semantic (else mapped) state meanwhile; null
    /// (the dash) when the mirror has no row.</summary>
    public static DisplayText? ServiceText(GsxServiceView? view) => view is null ? null
        : view.Stage == GsxServiceStage.Completed ? DisplayText.Id("flightStatus.services.completed")
        : DisplayText.Raw(view.SemanticState ?? view.MappedState);

    /// <summary>Live ground-preparation stage from the coordinator (issue #45). The pill
    /// carries a short status word only — the coordinator's detail sentence is log-grade and
    /// used to clip off the card edge — with the full "stage — detail" kept on the hover title.</summary>
    public static StatusRow GroundPrepRow(GsxGroundPrepView? prep)
    {
        var holding = prep is not null
            && prep.Detail.StartsWith("holding", StringComparison.OrdinalIgnoreCase);
        var tone = prep is null ? "tone-neutral"
            : prep.Stage == "Complete" ? "tone-ok"
            : holding ? "tone-warn"
            // Idle = parked chain (issue #98) — calm, not an activity and not a warning.
            : prep.Stage == "Idle" ? "tone-neutral"
            : prep.Detail.StartsWith("reset", StringComparison.OrdinalIgnoreCase) ? "tone-warn"
            : "tone-active";
        var text = prep is null ? null
            : holding ? DisplayText.Id("flightStatus.services.prep.holding")
            : PrepStageDisplay(prep.Stage);
        var tooltip = prep is null ? null : DisplayText.Id("flightStatus.services.prep.tooltip", PrepStageDisplay(prep.Stage), prep.Detail);
        return StatusRow.Pill("groundPrep", "flightStatus.services.groundPrep", tone, text, tooltip);
    }

    /// <summary>The coordinator publishes raw <c>GsxPrepStage</c> enum names ("AnchorGate",
    /// "GroundEquipment") — developer codenames, not dashboard words. Unknown names pass
    /// through so a future stage still renders something.</summary>
    public static DisplayText PrepStageDisplay(string stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        return stage switch
        {
            "Idle" => DisplayText.Id("flightStatus.services.prep.idle"),
            "Reposition" => DisplayText.Id("flightStatus.services.prep.reposition"),
            "Settling" => DisplayText.Id("flightStatus.services.prep.settling"),
            "AnchorGate" => DisplayText.Id("flightStatus.services.prep.anchorGate"),
            "GroundEquipment" => DisplayText.Id("flightStatus.services.prep.groundEquipment"),
            "JetwayStairs" => DisplayText.Id("flightStatus.services.prep.jetwayStairs"),
            "Complete" => DisplayText.Id("flightStatus.services.prep.complete"),
            _ => DisplayText.Raw(stage),
        };
    }

    /// <summary>Once-per-session cold-and-dark verdict (issue #63), published by the aircraft
    /// state check after session entry on a fresh departure. "—" until it has run. The pill
    /// stays a glanceable verdict; the item list rides on the hover title. The mismatch
    /// tooltip uses the spoken phrases ("battery 1 is on") — the checklist labels ("Battery 1
    /// OFF") read backwards when listed as what is WRONG. The Re-check button (issue #92)
    /// beside it is the markup's: it clears the once-per-session latch — without it, a pilot
    /// who fixed the switches could only get a fresh verdict by restarting the app.</summary>
    public static StatusRow AircraftStateRow(AircraftStateCheckView? check)
    {
        var (tone, text, tooltip) = check switch
        {
            null => ("tone-neutral", (DisplayText?)null, (DisplayText?)null),
            { Status: AircraftStateCheckStatus.Pass } pass =>
                ("tone-ok", DisplayText.Id("flightStatus.aircraftState.pass"),
                    pass.UncheckedLabels.Count == 0 ? null
                        : DisplayText.Id("flightStatus.aircraftState.unchecked", string.Join(", ", pass.UncheckedLabels))),
            { Status: AircraftStateCheckStatus.Mismatch } mismatch =>
                ("tone-warn",
                    DisplayText.Id(mismatch.Mismatches.Count == 1 ? "flightStatus.aircraftState.mismatchOne" : "flightStatus.aircraftState.mismatchMany", mismatch.Mismatches.Count),
                    DisplayText.Raw(string.Join(", ", mismatch.Mismatches.Select(item => item.Phrase)))),
            _ => ("tone-neutral", DisplayText.Id("flightStatus.aircraftState.notChecked"), DisplayText.RawOrNull(check.Reason)),
        };
        return StatusRow.Pill(AircraftStateRowId, "flightStatus.aircraftState.label", tone, text, tooltip);
    }

    /// <summary>Cabin-secure state (issue #134): the crew's randomized securing wait after
    /// doors closed + beacon on. "securing" is the moment a "cockpit to cabin" hail gets
    /// "still securing"; "secure" once the purser has reported. "—" before either.</summary>
    public static StatusRow CabinRow(CabinSecureState cabin)
    {
        var (tone, text, tooltip) = cabin switch
        {
            CabinSecureState.Securing => ("tone-warn", DisplayText.Id("flightStatus.cabin.securing"), DisplayText.Id("flightStatus.cabin.securingTitle")),
            CabinSecureState.Secure => ("tone-ok", DisplayText.Id("flightStatus.cabin.secure"), DisplayText.Id("flightStatus.cabin.secureTitle")),
            _ => ("tone-neutral", DisplayText.Dash, DisplayText.Id("flightStatus.cabin.noneTitle")),
        };
        return StatusRow.Pill("cabin", "flightStatus.cabin.label", tone, text, tooltip);
    }

    /// <summary>Refuel row (2026-09-19, real-world SOP): in gsx.refuelCall = onFuelConfirmed
    /// the truck waits for the crew's confirmation, so the pill says "awaiting fuel confirm"
    /// until it comes (or the departure completes); otherwise it is a plain service row. The
    /// confirm button and the confirmation pill (<see cref="FuelConfirmed"/>) are the
    /// markup's — the INIT page carries the same button next to the FUEL RAMP entry.</summary>
    public static StatusRow RefuelRow(GsxDiagnosticsSnapshot gsx, bool refuelSopMode, bool fuelConfirmed, bool departureComplete)
    {
        var view = ServiceView(gsx, GsxServiceIds.Refueling);
        var awaiting = refuelSopMode && !fuelConfirmed && !departureComplete;
        return awaiting
            ? StatusRow.Pill(RefuelRowId, "flightStatus.services.refuel", "tone-warn",
                DisplayText.Id("flightStatus.refuel.awaiting"), DisplayText.Id("flightStatus.refuel.awaitingTitle"))
            : StatusRow.Pill(RefuelRowId, "flightStatus.services.refuel", ServiceTone(view), ServiceText(view));
    }

    /// <summary>The green "N kg confirmed" pill after the refuel pill, or null before the
    /// crew has confirmed.</summary>
    public static FuelConfirmedPill? FuelConfirmed(FuelConfirmationSnapshot confirmation)
    {
        ArgumentNullException.ThrowIfNull(confirmation);
        return confirmation.Confirmed
            ? new FuelConfirmedPill(
                DisplayText.Id("flightStatus.refuel.confirmedKg", $"{confirmation.ConfirmedKg:F0}"),
                DisplayText.Id("flightStatus.refuel.confirmedTitle", $"{confirmation.ConfirmedAtUtc?.ToLocalTime():HH:mm:ss}", confirmation.Source ?? ""))
            : null;
    }

    /// <summary>A dropped GSX call (issue #76) stays visible until a later call confirms —
    /// the log line alone let a Deboarding call die unnoticed on 2026-08-29.</summary>
    public static StatusRow DroppedCallRow(GsxDroppedCallView? dropped)
    {
        var text = dropped is null
            ? DisplayText.Dash
            : string.IsNullOrWhiteSpace(dropped.OpenMenu)
                ? DisplayText.Id("flightStatus.gsx.dropped.checkMenu", dropped.ServiceId, $"{dropped.Timestamp.ToLocalTime():HH:mm:ss}")
                : DisplayText.Id("flightStatus.gsx.dropped.menuOpen", dropped.ServiceId, $"{dropped.Timestamp.ToLocalTime():HH:mm:ss}", dropped.OpenMenu);
        return StatusRow.Pill("droppedCall", "flightStatus.gsx.droppedCall",
            dropped is null ? "tone-neutral" : "tone-warn", text,
            DisplayText.Id(dropped is null ? "flightStatus.gsx.dropped.noneTitle" : "flightStatus.gsx.dropped.title"));
    }

    /// <summary>ProSim's ground-power dataref is the attachment truth (issue #33): GSX flips
    /// its GPU service back to "available" while the unit stays physically connected, so a
    /// connected GPU must never read as merely callable.</summary>
    public static StatusRow GpuRow(GsxDiagnosticsSnapshot gsx)
    {
        ArgumentNullException.ThrowIfNull(gsx);
        return gsx.GroundPowerConnected == true
            ? StatusRow.Pill("gpu", "flightStatus.services.gpu", "tone-ok", DisplayText.Id("flightStatus.services.gpuConnected"))
            : ServiceRow(gsx, "gpu", "flightStatus.services.gpu", GsxServiceIds.Gpu);
    }

    /// <summary>Docked = the operate service reads Active or Completed. The toggles never
    /// latch (their stage IS the positional mirror state), so retraction drops this back to
    /// "—". Refinement tracked in #30: LVAR-authoritative connect state with the 30 s
    /// stale-Active grace, once the operate LVARs are surfaced to the store.</summary>
    public static StatusRow ConnectedRow(GsxDiagnosticsSnapshot gsx, string id, string labelTextId, string serviceId)
    {
        var connected = ServiceView(gsx, serviceId)?.Stage is GsxServiceStage.Active or GsxServiceStage.Completed;
        return StatusRow.Pill(id, labelTextId, connected ? "tone-ok" : "tone-neutral",
            connected ? DisplayText.Id("flightStatus.services.connected") : null);
    }
    // ── Radios tile (owner request 2026-10-25) ───────────────────────────────────────

    /// <summary>The tile from the radio store: COM1, what ATC expects, the verdict class and
    /// the local airport's tap-to-tune stations. The verdict never claims "wrong" without
    /// both sides known: no SayIntentions, no assignment or no ProSim each have their own word.</summary>
    public static RadiosTileView Radios(RadioSnapshot radio, string? localIcao)
    {
        ArgumentNullException.ThrowIfNull(radio);

        var assigned = radio.Assigned;
        var (stateClass, stateTextId, hintTextId) = (radio.SayIntentionsOn, assigned, radio.OnAssignedFrequency) switch
        {
            (false, _, _) => ("off", "flightStatus.radios.off", "flightStatus.radios.offHint"),
            (true, null, _) => ("unknown", "flightStatus.radios.unknown", "flightStatus.radios.noAssignment"),
            (true, not null, null) => ("unknown", "flightStatus.radios.noProsim", null),
            (true, not null, true) => ("match", "flightStatus.radios.match", null),
            (true, not null, false) => ("mismatch", "flightStatus.radios.mismatch", null),
        };

        var stations = radio.StationsFor(localIcao);
        var chips = new List<RadioStationChip>();
        if (stations is not null)
        {
            foreach (var station in stations.Stations)
            {
                if (chips.Any(c => c.Khz == station.Khz))
                {
                    continue; // the same frequency twice (CLEARANCE and DELIVERY) is one chip
                }

                chips.Add(new RadioStationChip(
                    station.Type,
                    station.Callsign.Length > 0 ? station.Callsign : TitleCase(station.Type),
                    station.Khz,
                    RadioFrequency.Format(station.Khz),
                    IsActive: radio.Com1Live && station.Khz == radio.Com1ActiveKhz,
                    IsAssigned: assigned is not null && station.Khz == assigned.Khz));
            }
        }

        return new RadiosTileView(
            Active: radio.Com1Live ? RadioFrequency.Format(radio.Com1ActiveKhz) : RadioFrequency.Format(0),
            Standby: radio.Com1Live ? RadioFrequency.Format(radio.Com1StandbyKhz) : RadioFrequency.Format(0),
            Com1Live: radio.Com1Live,
            AssignedStation: assigned is null ? null : TitleCase(assigned.Station),
            AssignedFrequency: assigned is null ? null : RadioFrequency.Format(assigned.Khz),
            AssignedKhz: assigned?.Khz ?? 0,
            AssignedSince: assigned is null ? null : DisplayText.Id("flightStatus.radios.since", $"{assigned.SinceUtc:HH:mm}Z"),
            StateClass: stateClass,
            StateTextId: stateTextId,
            HintTextId: hintTextId,
            CanTuneAssigned: assigned is not null && radio.Com1Live && radio.OnAssignedFrequency == false,
            StationsIcao: stations?.Icao ?? "",
            Stations: chips,
            LastAtcLine: radio.LastAtcLine.Length > 0 ? radio.LastAtcLine : null,
            LastAtcSince: radio.LastAtcAtUtc is { } at ? DisplayText.Id("flightStatus.radios.since", $"{at:HH:mm}Z") : null);
    }

    /// <summary>"GROUND" → "Ground"; a mixed-case station name ("Tallinn Control") is kept.</summary>
    private static string TitleCase(string station)
    {
        var trimmed = station.Trim();
        return trimmed.Length > 1 && trimmed.All(c => !char.IsLetter(c) || char.IsUpper(c))
            ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(trimmed.ToLowerInvariant())
            : trimmed;
    }
}
