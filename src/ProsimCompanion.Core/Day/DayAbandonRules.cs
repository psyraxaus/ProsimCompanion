namespace ProsimCompanion.Core.Day;

/// <summary>
/// When a restored open duty day is no longer the day in progress (issue #239). A flight that
/// was given up part-way (a sim crash, the pilot stopped) leaves the day <c>OnLeg</c> with no
/// end: the leg never completes, and the idle backstop and the FO's offer
/// (<see cref="DayIdleRules"/>) count from deboarding, which never comes. On the owner's sim PC
/// the app resumed <c>day-20261107-194915</c> on 2026-11-14, one week old, and the owner
/// emptied <c>daystate.json</c> by hand to be rid of it.
/// <para>The rule is one clock in REAL time, judged once, at app start: an open day whose last
/// save is older than <see cref="StaleAfter"/> is not resumed — it is closed as abandoned. A
/// younger day resumes as before (the 2026-11-14 flight had three restarts within three hours).
/// Pure, with the clock and the last-save time passed in.</para>
/// </summary>
public static class DayAbandonRules
{
    /// <summary>The limit, in real time since the day state was last saved. Twelve hours is a
    /// judgement (owner has not chosen a design, 2026-11-14): longer than any A320 sector plus
    /// a long turnaround with the app closed, shorter than "the next day". One constant on
    /// purpose — no settings option.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(12);

    /// <summary>The age of the last save when the day must be abandoned, else null: no day, a
    /// closed day, an unknown last-save time (the day resumes, as before this rule) or a save
    /// younger than <see cref="StaleAfter"/>. A last save in the future (a clock set back) is
    /// not stale.</summary>
    /// <param name="lastSavedUtc">When the day state was last saved — today the state file's
    /// last-write time; a stamp inside the state would take its place here.</param>
    public static TimeSpan? StaleAge(DayState? day, DateTimeOffset? lastSavedUtc, DateTimeOffset nowUtc)
    {
        if (day is not { IsOpen: true } || lastSavedUtc is not { } saved)
        {
            return null;
        }

        var age = nowUtc - saved;
        return age > StaleAfter ? age : null;
    }
}
