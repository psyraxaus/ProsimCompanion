namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// Settings for the HTTP command API (<c>/api/commands</c>, <c>POST /api/command/{name}</c>) —
/// the seam the Stream Deck plugin and other remote clients fire commands through.
/// </summary>
public sealed class CommandApiOptions
{
    public const string SectionName = "commandApi";

    /// <summary>
    /// Master switch, off by default: unlike the read-only web pages this is a write surface
    /// (it actuates ground services, checklists, the MCDU), so it is strictly opt-in. When
    /// disabled every command route answers 404, as if it did not exist.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// When true (default), command routes require the web access token even from loopback —
    /// a deliberate delta from the page-serving middleware (where loopback always passes so the
    /// local UI can never be locked out): any local process can reach loopback, and "any local
    /// process may actuate the aircraft" is not an acceptable default for a write surface.
    /// </summary>
    public bool RequireTokenOnLoopback { get; set; } = true;
}
