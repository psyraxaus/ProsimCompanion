namespace ProsimCompanion.Core.Speech;

/// <summary>
/// Fix / navaid identifier → the name a crew says ("BPK" → "BROOKMANS PARK", "LAM" →
/// "LAMBOURNE"), owner request 2026-10-29: the briefing spelled the BPK7F departure letter by
/// letter where a crew says "Brookmans Park seven Foxtrot". A Core seam so the briefing and the
/// FO's radio check-in share one lookup without knowing where the names come from (the
/// SimBrief navlog first, the Navigraph DFD behind it). Null means "no name known" — callers
/// keep the phonetic fallback, so a missing OFP or DFD degrades, never fails. The raw form is
/// returned (ALL-CAPS as both sources store it); presentation is the caller's job.
/// </summary>
public interface IFixNames
{
    /// <summary>The stored name of <paramref name="ident"/> or null. <paramref name="nearAirportIcao"/>
    /// disambiguates an identifier several navaids share (three "LAM" VORs world-wide): a
    /// source that cannot tell them apart must return null rather than guess. Must never throw.</summary>
    string? Name(string ident, string? nearAirportIcao);
}
