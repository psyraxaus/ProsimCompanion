using System.Globalization;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.Speech;

namespace ProsimCompanion.Speech.Callouts;

/// <summary>Which altimeter reminder the FO gives.</summary>
public enum TransitionReminderKind
{
    /// <summary>Climbing past the transition altitude with the pilot's altimeter still on QNH.</summary>
    Standard,

    /// <summary>Descending past the transition level with the pilot's altimeter still on STD.</summary>
    Qnh,
}

/// <summary>One tick's worth of facts for <see cref="TransitionReminderCore"/>.</summary>
/// <param name="Phase">The committed flight phase.</param>
/// <param name="IsLive">The flight-live gate; nothing is armed while false.</param>
/// <param name="Enabled">The <c>sop.transition.enabled</c> switch, read per tick.</param>
/// <param name="IndicatedAltitudeFt">The pilot's own PFD altitude (issue #198) — QNH-corrected
/// below the transition, pressure altitude on STD — so each comparison is against the figure
/// the altimeter actually shows. 0 = not read.</param>
/// <param name="PilotOnStd">The pilot's effective STD gate; null when the dataref has not arrived.</param>
/// <param name="TransitionAltitudeFt">The departure airport's transition altitude (DFD), or null.</param>
/// <param name="TransitionLevel">The destination's transition level as a flight level (DFD), or null.</param>
/// <param name="QnhHpa">The destination QNH from its METAR, or null.</param>
/// <param name="Now">The clock, passed in so the core owns no timer.</param>
/// <param name="DestinationTransitionAltitudeFt">The destination's transition altitude (DFD),
/// or null — what the descent threshold is derived from when the database has no transition
/// level for the airport ("by ATC", the normal case in Europe).</param>
public sealed record TransitionReminderInput(
    FlightPhase Phase,
    bool IsLive,
    double IndicatedAltitudeFt,
    bool? PilotOnStd,
    double? TransitionAltitudeFt,
    double? TransitionLevel,
    double? QnhHpa,
    DateTimeOffset Now,
    double? DestinationTransitionAltitudeFt = null);

/// <summary>Where a reminder's threshold came from.</summary>
public enum TransitionThresholdSource
{
    /// <summary>The origin's transition altitude from the database (the climb reminder).</summary>
    DatabaseTransitionAltitude,

    /// <summary>The destination's transition level from the database.</summary>
    DatabaseTransitionLevel,

    /// <summary>No database level: the lowest flight level at or above the destination's
    /// transition altitude for the QNH of the day.</summary>
    DerivedFromQnh,

    /// <summary>No database level and no QNH: the destination's transition altitude plus
    /// <see cref="TransitionReminderCore.NoQnhMarginFt"/>.</summary>
    DerivedFromMargin,
}

/// <summary>The altitude below which the descent reminder is due, and how it was found.</summary>
/// <param name="ThresholdFt">Feet on the pilot's altimeter while it is on STD.</param>
/// <param name="Source">Database level, or one of the two derivations.</param>
/// <param name="TransitionAltitudeFt">The destination transition altitude a derivation used.</param>
/// <param name="QnhHpa">The QNH a derivation used.</param>
public readonly record struct DescentThreshold(
    double ThresholdFt,
    TransitionThresholdSource Source,
    double? TransitionAltitudeFt,
    double? QnhHpa)
{
    /// <summary>"database TL" / "derived from TA 5000 ft and QNH 1022" / "derived from TA
    /// 5000 ft plus 1000 ft (no QNH)" — the log line and the event's <c>thresholdBasis</c>.</summary>
    public string Describe() => TransitionReminderCore.DescribeSource(Source, TransitionAltitudeFt, QnhHpa);
}

/// <summary>A reminder the core decided to give this tick.</summary>
public sealed record TransitionReminder(
    TransitionReminderKind Kind,
    string Text,
    double AltitudeFt,
    double ThresholdFt,
    double? QnhHpa,
    TransitionThresholdSource Source = TransitionThresholdSource.DatabaseTransitionAltitude,
    string ThresholdBasis = "database TA");

/// <summary>
/// The pure decision behind the FO's altimeter reminders (issue #198, owner request
/// 2026-11-01). Climbing through the transition altitude still on QNH earns "Transition
/// altitude. Standard, please."; descending through the transition level still on STD earns
/// "Transition level. QNH one zero one three, please." Each fires once per crossing and only
/// after the condition has held for the patience delay — the pilot normally sets the altimeter
/// first, and a reminder that beats them to it is a nag. Setting the altimeter inside the
/// delay silently disarms it. No transition figure (no Navigraph database, airport without
/// one) means no reminder on that side — never a guessed 18,000 ft. The QNH reminder re-arms
/// when the aircraft climbs again (go-around), the standard reminder when it descends.
///
/// The descent threshold (owner's flight EGLL-LOWW 2026-11-14: "LOWW TL null", no descent
/// reminder): most European airports have no transition level in the database because ATC
/// assigns it. The level is then derived from the destination's transition altitude — see
/// <see cref="ResolveDescentThreshold"/>. An airport with neither figure stays silent.
/// </summary>
public sealed class TransitionReminderCore
{
    public const string StandardText = "Transition altitude. Standard, please.";
    public const string QnhUnknownText = "Transition level. QNH, please.";

    /// <summary>Added to the destination's transition altitude when no QNH is known: the
    /// customary minimum transition layer. With a QNH between 980 and 1013 hPa the derived
    /// level lies up to 1,900 ft above the altitude, above 1013 at or just above it — a
    /// reminder some hundred feet early or late, never a wrong altimeter setting.</summary>
    public const double NoQnhMarginFt = 1000;

    /// <summary>The descent threshold. In order: the database's transition level; else, from
    /// the destination's transition altitude and QNH, the lowest whole-thousand flight level
    /// (FL50, FL60 …) whose pressure altitude is at or above the transition altitude — the
    /// aircraft is on STD, so its altimeter shows pressure altitude, and the transition
    /// altitude lies at <see cref="PressureAltitudeFt"/> on that scale; else the transition
    /// altitude plus <see cref="NoQnhMarginFt"/>; else null (no figure, no reminder). ATC may
    /// assign a level one step higher (a guaranteed 1,000 ft layer) — the derived one is the
    /// lowest it can be, so the reminder is late rather than early.</summary>
    public static DescentThreshold? ResolveDescentThreshold(double? transitionLevel, double? destinationTransitionAltitudeFt, double? qnhHpa)
    {
        if (transitionLevel is { } tl && tl > 0)
        {
            return new DescentThreshold(tl * 100, TransitionThresholdSource.DatabaseTransitionLevel, null, null);
        }

        if (destinationTransitionAltitudeFt is not { } ta || ta <= 0)
        {
            return null;
        }

        if (qnhHpa is { } qnh && qnh is >= 900 and <= 1100)
        {
            var level = Math.Ceiling(Math.Round(PressureAltitudeFt(ta, qnh)) / 1000) * 1000;
            return new DescentThreshold(level, TransitionThresholdSource.DerivedFromQnh, ta, qnh);
        }

        return new DescentThreshold(ta + NoQnhMarginFt, TransitionThresholdSource.DerivedFromMargin, ta, null);
    }

    /// <summary>The pressure altitude (what an altimeter on 1013.25 shows) at an altitude
    /// read on <paramref name="qnhHpa"/> — the ISA altimeter equation, about 27 ft per hPa
    /// near sea level.</summary>
    public static double PressureAltitudeFt(double altitudeOnQnhFt, double qnhHpa)
        => altitudeOnQnhFt + (145366.45 * (1 - Math.Pow(qnhHpa / 1013.25, 0.190284)));

    /// <summary>The threshold's origin in words, for the log and the event.</summary>
    public static string DescribeSource(TransitionThresholdSource source, double? transitionAltitudeFt, double? qnhHpa)
    {
        var ta = (transitionAltitudeFt ?? 0).ToString("0", CultureInfo.InvariantCulture);
        return source switch
        {
            TransitionThresholdSource.DatabaseTransitionLevel => "database TL",
            TransitionThresholdSource.DerivedFromQnh => $"derived from TA {ta} ft and QNH {(qnhHpa ?? 0).ToString("0", CultureInfo.InvariantCulture)}",
            TransitionThresholdSource.DerivedFromMargin => $"derived from TA {ta} ft plus {NoQnhMarginFt.ToString("0", CultureInfo.InvariantCulture)} ft (no QNH)",
            _ => "database TA",
        };
    }

    private DateTimeOffset? _standardSince;
    private DateTimeOffset? _qnhSince;
    private bool _standardSpoken;
    private bool _qnhSpoken;

    /// <summary>The reminder due this tick, or null.</summary>
    public TransitionReminder? Process(TransitionReminderInput input, TimeSpan delay)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.IsLive || input.IndicatedAltitudeFt <= 0 || input.PilotOnStd is not { } onStd)
        {
            _standardSince = null;
            _qnhSince = null;
            return null;
        }

        // Re-arm the other side's reminder once the aircraft has clearly gone the other way.
        if (input.Phase is FlightPhase.Descent or FlightPhase.Approach)
        {
            _standardSpoken = false;
        }
        else if (input.Phase is FlightPhase.Climb or FlightPhase.Cruise)
        {
            _qnhSpoken = false;
        }

        var climbing = input.Phase is FlightPhase.InitialClimb or FlightPhase.Climb or FlightPhase.Cruise;
        var standardDue = climbing && !_standardSpoken && !onStd
            && input.TransitionAltitudeFt is { } ta && ta > 0 && input.IndicatedAltitudeFt > ta;
        if (standardDue)
        {
            _standardSince ??= input.Now;
            if (input.Now - _standardSince.Value >= delay)
            {
                _standardSpoken = true;
                _standardSince = null;
                return new TransitionReminder(TransitionReminderKind.Standard, StandardText, input.IndicatedAltitudeFt, input.TransitionAltitudeFt!.Value, null);
            }
        }
        else
        {
            _standardSince = null;
        }

        var descending = input.Phase is FlightPhase.Descent or FlightPhase.Approach;
        var threshold = ResolveDescentThreshold(input.TransitionLevel, input.DestinationTransitionAltitudeFt, input.QnhHpa);
        var qnhDue = descending && !_qnhSpoken && onStd
            && threshold is { } below && input.IndicatedAltitudeFt < below.ThresholdFt;
        if (qnhDue)
        {
            _qnhSince ??= input.Now;
            if (input.Now - _qnhSince.Value >= delay)
            {
                _qnhSpoken = true;
                _qnhSince = null;
                var used = threshold!.Value;
                return new TransitionReminder(
                    TransitionReminderKind.Qnh, QnhText(input.QnhHpa), input.IndicatedAltitudeFt, used.ThresholdFt, input.QnhHpa,
                    used.Source, used.Describe());
            }
        }
        else
        {
            _qnhSince = null;
        }

        return null;
    }

    /// <summary>Forgets every latch — a new flight cycle or sim session.</summary>
    public void Reset()
    {
        _standardSince = null;
        _qnhSince = null;
        _standardSpoken = false;
        _qnhSpoken = false;
    }

    /// <summary>"Transition level. QNH one zero one three, please." — digits, as a QNH is read.</summary>
    public static string QnhText(double? qnhHpa)
    {
        if (qnhHpa is not { } hpa || hpa <= 0)
        {
            return QnhUnknownText;
        }

        var digits = ((int)Math.Round(hpa)).ToString(CultureInfo.InvariantCulture);
        var words = string.Join(' ', digits.Select(c => NatoPhonetics.DigitWords[c - '0']));
        return $"Transition level. QNH {words}, please.";
    }
}
