using System.Globalization;

namespace ProsimCompanion.Core.Persona;

/// <summary>Everything the trigger rule needs for one evaluation.</summary>
/// <param name="RosterActive">Persona on and the roster mode daily or pinned.</param>
/// <param name="Pinned">Pinned mode: the day stamp moves, nobody is drawn.</param>
/// <param name="OpenDayLegsCompleted">Legs already flown (on blocks) in the OPEN duty day;
/// null when no duty day is open. One or more means the day is past its first leg: the FO on
/// duty is pinned until end of duty whatever the calendar does (ADR-0019 §8).</param>
/// <param name="OpenDayId">The open duty day's id; null when no day is open.</param>
/// <param name="DrawnDayId">The day the FO on duty was drawn for or bound to
/// (<c>FoRosterState.DrawnDayId</c>). Equal to <paramref name="OpenDayId"/> means the FO is
/// this day's FO and stays pinned while it is open, flown leg or not (review 2026-11-05: an
/// open leg-1 day restarted after a sim-date rollover redrew — mid-day by #219's definition).</param>
/// <param name="LegBegan">This evaluation is the "a new leg began" edge — a different OFP
/// landed (<c>ILegBoundaryEvents.LegBegan</c>).</param>
/// <param name="LegPlanned">A plan is loaded and the leg boundary is None: the loaded plan is
/// the leg being prepared, not the one just flown (a Turnaround / Awaiting plan state means
/// the plan in the store is the arrival's — never a draw opportunity).</param>
/// <param name="InPreflight">The committed phase is Preflight right now.</param>
/// <param name="SimLocalDate">The sim's local calendar day at the aircraft; null when the sim
/// is not live (the wall-clock UTC date stands in, logged once by the caller).</param>
/// <param name="FallbackDate">The UTC wall date for the stand-in.</param>
/// <param name="DrawnFor">The persisted stamp of the current draw.</param>
/// <param name="HasCurrent">An FO is on duty after any draw this evaluation makes.</param>
/// <param name="IntroDone">The gate intro has been spoken for the current draw.</param>
/// <param name="IntroEnabled"><c>persona.roster.gateIntro</c>.</param>
public sealed record FoRosterTriggerInputs(
    bool RosterActive,
    bool Pinned,
    int? OpenDayLegsCompleted,
    string? OpenDayId,
    string? DrawnDayId,
    bool LegBegan,
    bool LegPlanned,
    bool InPreflight,
    DateOnly? SimLocalDate,
    DateOnly FallbackDate,
    string? DrawnFor,
    bool HasCurrent,
    bool IntroDone,
    bool IntroEnabled);

/// <summary>What one evaluation decided, in order: a new day stamp to draw (or adopt, when
/// pinned) for, then whether the gate intro is due.</summary>
/// <param name="NewStamp">The stamp a new draw is for; null = no new day.</param>
/// <param name="Reason">The draw reason for the event ("new-leg" | "preflight").</param>
/// <param name="Intro">Speak the gate intro now.</param>
public sealed record FoRosterTriggerDecision(string? NewStamp, string? Reason, bool Intro)
{
    public static FoRosterTriggerDecision Nothing { get; } = new(null, null, false);
}

/// <summary>
/// The pure trigger rule of the roster (issue #194, narrowed by ADR-0019 §8 / issue #219):
/// <b>one First Officer per sim calendar day, never per day id, never mid-day.</b> A draw
/// needs three things at once — an opportunity (a new leg began: a different OFP landed; or
/// the aircraft is in Preflight with that leg's plan loaded, for the app that started after
/// the plan did), a sim-local date that differs from the stamp of the last draw, and no open
/// duty day past its first leg (an open day pins its FO until end of duty — EHAM 2026-11-01:
/// the sim date rolled over in the turnaround and Hanna became Sam mid-day). A day id change
/// on the same sim date keeps the FO; a plan loaded after an end of duty on the same date
/// keeps the FO; a new sim date with a new plan draws. The stamp is always <c>sim:yyyy-MM-dd</c>
/// (the sim's own date, the rc.23 greeting clock, not wall time). The intro is due once per
/// stamp, in Preflight, whether the draw happened at this edge or earlier (a plan loaded cold
/// and dark greets at the Preflight edge).
/// </summary>
public static class FoRosterTriggerCore
{
    public static FoRosterTriggerDecision Evaluate(FoRosterTriggerInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (!inputs.RosterActive)
        {
            return FoRosterTriggerDecision.Nothing;
        }

        string? newStamp = null;
        string? reason = null;
        var opportunity = inputs.LegBegan || (inputs.InPreflight && inputs.LegPlanned);
        // An open day pins its FO: once a leg has been flown, or from the moment the FO on
        // duty is this day's (drawn in it, or bound to it on the same sim date). A day that
        // opened with yesterday's FO still on file is not that FO's day — the first leg of a
        // new sim date must draw.
        var dayPinsFo = inputs.HasCurrent && inputs.OpenDayId is not null
            && (inputs.OpenDayLegsCompleted is > 0 || string.Equals(inputs.OpenDayId, inputs.DrawnDayId, StringComparison.Ordinal));
        if (opportunity && !dayPinsFo)
        {
            var date = inputs.SimLocalDate ?? inputs.FallbackDate;
            var stamp = Stamp(date);
            if (!string.Equals(stamp, inputs.DrawnFor, StringComparison.Ordinal))
            {
                newStamp = stamp;
                reason = inputs.LegBegan ? "new-leg" : "preflight";
            }
        }

        var hasStamp = newStamp is not null || inputs.DrawnFor is not null;
        var introPending = newStamp is not null || !inputs.IntroDone;
        var intro = inputs.IntroEnabled && inputs.InPreflight && hasStamp && introPending && inputs.HasCurrent;
        return new FoRosterTriggerDecision(newStamp, reason, intro);
    }

    /// <summary>The draw stamp for a sim calendar day.</summary>
    public static string Stamp(DateOnly simLocalDate)
        => "sim:" + simLocalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
