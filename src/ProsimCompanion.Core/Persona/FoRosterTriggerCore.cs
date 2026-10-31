using System.Globalization;

namespace ProsimCompanion.Core.Persona;

/// <summary>Everything the trigger rule needs for one evaluation.</summary>
/// <param name="RosterActive">Persona on and the roster mode daily or pinned.</param>
/// <param name="Pinned">Pinned mode: the day stamp moves, nobody is drawn.</param>
/// <param name="DayModeOn">Company day mode is enabled: the duty day decides the draw.</param>
/// <param name="OpenDayId">The open duty day's id, or null when none is open.</param>
/// <param name="PreflightEdge">This evaluation is the committed edge INTO Preflight.</param>
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
    bool DayModeOn,
    string? OpenDayId,
    bool PreflightEdge,
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
/// <param name="Reason">The draw reason for the event ("day.started" | "sim-day").</param>
/// <param name="Intro">Speak the gate intro now.</param>
public sealed record FoRosterTriggerDecision(string? NewStamp, string? Reason, bool Intro)
{
    public static FoRosterTriggerDecision Nothing { get; } = new(null, null, false);
}

/// <summary>
/// The pure trigger rule of the roster (issue #194): WHEN a new FO is drawn and WHEN the gate
/// intro is spoken. With day mode on, the open duty day is the unit — a new day id draws, the
/// same id (a restart mid-rotation) keeps the FO. With day mode off, the sim's local calendar
/// day at the first Preflight of a session is the unit (the rc.23 greeting clock, not wall
/// time). The intro is due once per stamp, in Preflight, whether the draw happened at this
/// edge or earlier (a day started by voice after the Preflight edge still greets).
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
        if (inputs.DayModeOn)
        {
            if (inputs.OpenDayId is { Length: > 0 } dayId)
            {
                var stamp = "day:" + dayId;
                if (!string.Equals(stamp, inputs.DrawnFor, StringComparison.Ordinal))
                {
                    newStamp = stamp;
                    reason = "day.started";
                }
            }
        }
        else if (inputs.PreflightEdge)
        {
            var date = inputs.SimLocalDate ?? inputs.FallbackDate;
            var stamp = "sim:" + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (!string.Equals(stamp, inputs.DrawnFor, StringComparison.Ordinal))
            {
                newStamp = stamp;
                reason = "sim-day";
            }
        }

        var hasStamp = newStamp is not null || inputs.DrawnFor is not null;
        var introPending = newStamp is not null || !inputs.IntroDone;
        var intro = inputs.IntroEnabled && inputs.InPreflight && hasStamp && introPending && inputs.HasCurrent;
        return new FoRosterTriggerDecision(newStamp, reason, intro);
    }
}
