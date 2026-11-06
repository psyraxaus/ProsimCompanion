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
public sealed record TransitionReminderInput(
    FlightPhase Phase,
    bool IsLive,
    double IndicatedAltitudeFt,
    bool? PilotOnStd,
    double? TransitionAltitudeFt,
    double? TransitionLevel,
    double? QnhHpa,
    DateTimeOffset Now);

/// <summary>A reminder the core decided to give this tick.</summary>
public sealed record TransitionReminder(
    TransitionReminderKind Kind,
    string Text,
    double AltitudeFt,
    double ThresholdFt,
    double? QnhHpa);

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
/// </summary>
public sealed class TransitionReminderCore
{
    public const string StandardText = "Transition altitude. Standard, please.";
    public const string QnhUnknownText = "Transition level. QNH, please.";

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
        var qnhDue = descending && !_qnhSpoken && onStd
            && input.TransitionLevel is { } tl && tl > 0 && input.IndicatedAltitudeFt < tl * 100;
        if (qnhDue)
        {
            _qnhSince ??= input.Now;
            if (input.Now - _qnhSince.Value >= delay)
            {
                _qnhSpoken = true;
                _qnhSince = null;
                return new TransitionReminder(TransitionReminderKind.Qnh, QnhText(input.QnhHpa), input.IndicatedAltitudeFt, input.TransitionLevel!.Value * 100, input.QnhHpa);
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
