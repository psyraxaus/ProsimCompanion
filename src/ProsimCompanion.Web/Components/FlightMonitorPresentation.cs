using System.Globalization;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Boarding;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.Weather;

namespace ProsimCompanion.Web.Components;

/// <summary>Which face the pop-out board shows (owner spec 2026-09-23).</summary>
public enum MonitorMode
{
    /// <summary>At the stand before the push: the gate monitor.</summary>
    Gate,

    /// <summary>Pushback through the landing roll: the flight monitor.</summary>
    Flight,

    /// <summary>Taxi-in and shutdown: the arrival monitor.</summary>
    Arrival,
}

/// <summary>
/// Pure presentation rules of the pop-out Flight Monitor board — the mode, the big state
/// word, the progress line and the ETA — kept out of the page so they are testable and so
/// the board and any future surface (Stream Deck, voice) agree on what the leg "is".
/// </summary>
public static class FlightMonitorPresentation
{
    /// <summary>Design size of the board; the browser scales it as one piece to the window.</summary>
    public const int StageWidth = 1920;

    public const int StageHeight = 1080;

    public static MonitorMode Mode(FlightPhase phase) => phase switch
    {
        FlightPhase.TaxiIn or FlightPhase.Shutdown => MonitorMode.Arrival,
        FlightPhase.PushbackAndStart or FlightPhase.TaxiOut or FlightPhase.TakeoffRoll
            or FlightPhase.InitialClimb or FlightPhase.Climb or FlightPhase.Cruise
            or FlightPhase.Descent or FlightPhase.Approach or FlightPhase.LandingRollout => MonitorMode.Flight,
        _ => MonitorMode.Gate,
    };

    public static string ModeLabel(MonitorMode mode) => mode switch
    {
        MonitorMode.Flight => "Flight monitor",
        MonitorMode.Arrival => "Arrival monitor",
        _ => "Gate monitor",
    };

    /// <summary>The big word. Gate mode reads the gate monitor; flight mode reads the
    /// phase; arrival mode reads the deboarding.</summary>
    public static string StateWord(MonitorMode mode, GateState gate, FlightPhase phase, bool deboardingActive, bool arrivalComplete)
        => mode switch
        {
            MonitorMode.Gate => gate switch
            {
                GateState.Open => "GATE OPEN",
                GateState.Boarding => "BOARDING",
                GateState.FinalCall => "FINAL CALL",
                _ => "GATE CLOSED",
            },
            MonitorMode.Flight => FlightStatusPresentation.PhaseDisplay(phase),
            _ => phase == FlightPhase.TaxiIn ? "TAXI IN"
                : arrivalComplete ? "ARRIVED"
                : deboardingActive ? "DEBOARDING"
                : "ON BLOCKS",
        };

    /// <summary>CSS tone of the state word: matches the gate strip's five tones on the
    /// ground, cyan in the air, green once the arrival is complete.</summary>
    public static string StateTone(MonitorMode mode, GateState gate, bool arrivalComplete) => mode switch
    {
        MonitorMode.Gate => gate switch
        {
            GateState.Open => "open",
            GateState.Boarding => "boarding",
            GateState.FinalCall => "final-call",
            GateState.ClosedAfterBoarding => "closed-after",
            _ => "closed",
        },
        MonitorMode.Flight => "boarding",
        _ => arrivalComplete ? "closed-after" : "boarding",
    };

    /// <summary>The TIME-BASED progress share (off-blocks against the OFP enroute time) —
    /// since issue #145 the fallback behind the position-based fraction on
    /// <see cref="FlightProgressStore"/>. The rule itself lives in
    /// <see cref="FlightProgressCore.TimeFraction"/>; this forwards so the board's direct
    /// render (before the store's first tick) and the store can never disagree.</summary>
    public static double? FlightProgress(FlightTimesSnapshot times, TimeSpan? estimatedEnroute, DateTimeOffset nowUtc)
        => FlightProgressCore.TimeFraction(times, estimatedEnroute, nowUtc);

    /// <summary>The TIME-BASED estimated on-blocks (see <see cref="FlightProgressCore.TimeEta"/>):
    /// the fallback behind the ground-speed ETA.</summary>
    public static DateTimeOffset? Eta(FlightTimesSnapshot times, OfpData? ofp, DateTimeOffset? stdUtc)
        => FlightProgressCore.TimeEta(times, ofp, stdUtc);

    /// <summary>"312 NM" / "—". Whole miles: the figure is a direct distance, and a decimal
    /// would claim a precision the route does not have.</summary>
    public static string Distance(double? nm)
        => nm is { } value && double.IsFinite(value)
            ? Math.Round(value).ToString("N0", CultureInfo.InvariantCulture) + " NM"
            : "—";

    /// <summary>"14 MIN" / "NOW" / "—" for the minutes-to-top-of-descent figure.</summary>
    public static string MinutesToTod(double? minutes)
        => minutes switch
        {
            null => "—",
            < 0.5 => "NOW",
            { } value => Math.Round(value).ToString("F0", CultureInfo.InvariantCulture) + " MIN",
        };

    /// <summary>The tag after an ETA: where the figure came from.</summary>
    public static string BasisTag(ProgressBasis basis) => basis switch
    {
        ProgressBasis.Position => "GS",
        ProgressBasis.Time => "PLAN",
        _ => "",
    };

    /// <summary>The one-line honesty label under the route strip and on Flight Status.</summary>
    public static string ProgressCaption(FlightProgressSnapshot progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        return progress.FractionBasis switch
        {
            ProgressBasis.Position => "Great-circle direct · from position",
            ProgressBasis.Time => progress.Position is null
                ? "Time-based · no aircraft position"
                : "Time-based · airport position unknown",
            _ => "No progress data",
        };
    }

    /// <summary>Lays the leg out on the board's route strip (issue #145): the origin →
    /// destination great circle drawn as a straight line, the aircraft at its along-track /
    /// cross-track position on ONE scale (so an off-route marker is honestly off the line),
    /// the marker turned by its track relative to the route. Without a position the marker
    /// rides the line at the time-based fraction; with no figure at all it is absent.</summary>
    public static RouteStripView RouteStrip(FlightProgressSnapshot progress)
    {
        ArgumentNullException.ThrowIfNull(progress);

        const double left = RouteStripView.OriginX;
        const double right = RouteStripView.DestinationX;
        const double span = right - left;

        if (progress is { RouteDistanceNm: > 0, AlongTrackNm: { } along, CrossTrackNm: { } cross } located
            && located.Origin is { } origin && located.Destination is { } destination)
        {
            var route = located.RouteDistanceNm!.Value;
            var scale = span / route;
            var x = Math.Clamp(left + (along * scale), RouteStripView.Edge, RouteStripView.Width - RouteStripView.Edge);
            var y = Math.Clamp(
                RouteStripView.LineY + (cross * scale),
                RouteStripView.Edge, RouteStripView.Height - RouteStripView.Edge);
            // Owner decision 2026-10-04: the plane always points along the strip towards the
            // destination — never turned by the live track. At the gate the aircraft sits on
            // whatever heading the stand has (145° in the photo) and the marker pointed away
            // from the route; in flight the track rarely differs enough to read. The track is
            // still in the view model for anything that wants it later.
            const double rotation = 0.0;

            double? tod = located.DescentDistanceNm is { } descent && descent < route
                ? left + ((route - descent) * scale)
                : null;
            return new RouteStripView(x, y, rotation, Math.Clamp(x, left, right), tod);
        }

        if (progress.Fraction is { } fraction)
        {
            var x = left + (Math.Clamp(fraction, 0, 1) * span);
            return new RouteStripView(x, RouteStripView.LineY, 0, x, null);
        }

        return new RouteStripView(null, null, 0, null, null);
    }

    /// <summary>"2h 15m" / "48m" / "—".</summary>
    public static string Duration(TimeSpan? span)
    {
        if (span is not { } value || value < TimeSpan.Zero)
        {
            return "—";
        }

        var minutes = (int)Math.Round(value.TotalMinutes);
        return minutes >= 60 ? $"{minutes / 60}h {minutes % 60:00}m" : $"{minutes}m";
    }

    /// <summary>"13:00Z" / "—".</summary>
    public static string Clock(DateTimeOffset? utc)
        => utc is { } value ? value.ToString("HH:mm", CultureInfo.InvariantCulture) + "Z" : "—";

    /// <summary>The deboarding share, 0–1, for the arrival bar; 0 when unknown.</summary>
    public static double DeboardingProgress(int? paxDeboarded, int? paxTarget)
        => paxTarget is > 0 && paxDeboarded is { } off ? Math.Clamp((double)off / paxTarget.Value, 0, 1) : 0;
}

/// <summary>Geometry of the board's route strip in its own SVG user units (the viewBox is
/// <see cref="Width"/> × <see cref="Height"/>). Null marker = nothing to place.</summary>
/// <param name="MarkerX">Aircraft marker centre.</param>
/// <param name="MarkerY">Aircraft marker centre; <see cref="LineY"/> = on the route.</param>
/// <param name="MarkerRotationDeg">Clockwise turn of the marker; 0 = along the route.</param>
/// <param name="FlownX">Right end of the "flown" part of the line.</param>
/// <param name="TodX">The estimated top-of-descent tick, when there is one.</param>
public sealed record RouteStripView(double? MarkerX, double? MarkerY, double MarkerRotationDeg, double? FlownX, double? TodX)
{
    public const double Width = 1200;
    public const double Height = 72;
    public const double LineY = 36;
    public const double OriginX = 40;
    public const double DestinationX = 1160;

    /// <summary>Closest the marker centre may come to the strip's border.</summary>
    public const double Edge = 12;

    /// <summary>Invariant "123.4" for an SVG attribute — a comma decimal breaks the markup.</summary>
    public static string Px(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);
}

/// <summary>The weather-card figure texts, shared by the Flight Status hero and the board so
/// the two never format the same METAR differently.</summary>
public static class WeatherCardFormat
{
    public static string Wind(WxFacts facts) => facts switch
    {
        { WindSpeedKt: null } => "WIND —",
        { WindSpeedKt: 0 } => "CALM",
        { WindDirDeg: null } => $"VRB/{facts.WindSpeedKt:00} KT",
        _ => $"{facts.WindDirDeg:000}°/{facts.WindSpeedKt:00} KT",
    };

    public static string Visibility(WxFacts facts) => facts.VisibilityMeters switch
    {
        null => "VIS —",
        >= 10000 => "10 KM+",
        >= 1000 => string.Create(CultureInfo.InvariantCulture, $"{facts.VisibilityMeters.Value / 1000.0:0.#} KM"),
        _ => $"{facts.VisibilityMeters} M",
    };

    public static string Ceiling(WxFacts facts)
        => facts.CeilingFt is { } ceiling ? string.Create(CultureInfo.InvariantCulture, $"CEILING {ceiling:N0} FT") : "NO CEILING";

    public static string Temperature(WxFacts facts)
        => facts.TemperatureC is { } temp ? string.Create(CultureInfo.InvariantCulture, $"{temp:+0;-0;0}°C") : "TEMP —";

    public static string Qnh(WxFacts facts)
        => facts.QnhHpa is { } qnh ? string.Create(CultureInfo.InvariantCulture, $"Q{qnh:0}") : "QNH —";

    public static string SkyIcon(SkyCondition sky) => sky switch
    {
        SkyCondition.Clear => "sun",
        SkyCondition.FewClouds => "cloud-sun",
        SkyCondition.Overcast => "cloud",
        SkyCondition.Rain => "cloud-rain",
        SkyCondition.Drizzle => "cloud-drizzle",
        SkyCondition.Snow => "cloud-snow",
        SkyCondition.Fog => "cloud-fog",
        SkyCondition.Thunderstorm => "cloud-lightning",
        SkyCondition.Windy => "wind",
        _ => "help-circle",
    };
}
