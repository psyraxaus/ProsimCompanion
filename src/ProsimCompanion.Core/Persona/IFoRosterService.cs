namespace ProsimCompanion.Core.Persona;

/// <summary>
/// The FO roster (issue #194): the pool from <c>fo-roster.json</c>, the FO on duty, and the
/// three things a caller may do — draw, pin, release. Core-owned so the Web project (Roster
/// card) and the Speech pillar (persona fragment, voice, gate intro, voice commands) share one
/// store; the triggers that decide WHEN to draw live in the Speech feature, the dice and the
/// persistence here.
/// </summary>
public interface IFoRosterService
{
    /// <summary>The FO on duty: the pinned one in pinned mode, else the last draw; null before
    /// the first draw, when the roster is off, or when the drawn id is no longer in the file.</summary>
    FoRosterEntry? Current { get; }

    /// <summary>The roster as loaded (reloaded on file change).</summary>
    IReadOnlyList<FoRosterEntry> Pool { get; }

    /// <summary>The persisted draw bookkeeping (a copy).</summary>
    FoRosterState State { get; }

    /// <summary>True when the roster decides who the FO is (persona on, mode daily or pinned).</summary>
    bool IsActive { get; }

    /// <summary>Where the user-editable file lives (the Roster card names it).</summary>
    string FilePath { get; }

    /// <summary>Raised after any change to the pool, the draw or the pin, on the writer's thread.</summary>
    event Action? Changed;

    /// <summary>Draws a new FO (never the previous one when the pool allows), stamps what the
    /// draw is for and resets the intro latch. Null when the pool is empty. <paramref name="reason"/>
    /// is recorded on the <c>persona.rosterDraw</c> event ("new-leg", "preflight", "voice", "web").
    /// <paramref name="dayId"/> is the open duty day the draw belongs to (null = none open): the
    /// FO is pinned while that day is open (ADR-0019 §8).</summary>
    FoRosterEntry? Draw(string reason, DateTimeOffset nowUtc, string? drawnFor = null, string? dayId = null);

    /// <summary>Adopts a new day stamp without drawing (pinned mode): the intro latch resets
    /// so the pinned FO greets once per day too.</summary>
    void AdoptDay(string drawnFor, string? dayId = null);

    /// <summary>Binds the FO on duty to the duty day that opened after the draw on the same
    /// sim date — the FO stays pinned while it is open. No-op without an FO or when already bound.</summary>
    void BindDay(string dayId);

    /// <summary>Pins one FO and switches the mode to pinned. False when the id is unknown.</summary>
    bool Pin(string id);

    /// <summary>Clears the pin and switches the mode back to daily.</summary>
    void Release();

    /// <summary>The gate intro has been spoken for the current draw.</summary>
    void MarkIntroSpoken();
}
