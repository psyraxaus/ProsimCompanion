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

    /// <summary>Share of the planned block time flown, 0–1, from the off-blocks stamp and the
    /// OFP's estimated enroute time. Null until off-blocks (the bar then shows the pax bar in
    /// gate mode). Position data does not exist in the app, so this is time-based by design
    /// (2026-09-23 feasibility scan); a leg that runs long pins at 100 %.</summary>
    public static double? FlightProgress(FlightTimesSnapshot times, TimeSpan? estimatedEnroute, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(times);
        if (times.OffBlocksUtc is not { } off || estimatedEnroute is not { } eet || eet <= TimeSpan.Zero)
        {
            return null;
        }

        var end = times.OnBlocksUtc ?? nowUtc;
        return Math.Clamp((end - off) / eet, 0, 1);
    }

    /// <summary>Estimated on-blocks: takeoff + enroute once airborne, else off-blocks +
    /// enroute, else STD + enroute (the OFP page's rule) — whichever is the best evidence.</summary>
    public static DateTimeOffset? Eta(FlightTimesSnapshot times, OfpData? ofp, DateTimeOffset? stdUtc)
    {
        ArgumentNullException.ThrowIfNull(times);
        if (times.OnBlocksUtc is { } on)
        {
            return on;
        }

        if (ofp?.EstimatedEnroute is not { } eet)
        {
            return null;
        }

        var anchor = times.TakeoffUtc ?? times.OffBlocksUtc ?? stdUtc;
        return anchor?.Add(eet);
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
        => facts.CeilingFt is { } ceiling ? string.Create(CultureInfo.InvariantCulture, $"CIG {ceiling:N0} FT") : "NO CIG";

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
