namespace ProsimCompanion.Core.Airports;

/// <summary>
/// ICAO → spoken airport name ("EGLL" → "Heathrow", "EGPH" → "Edinburgh"), issue #70. A Core
/// seam so every speaker (briefings, company channel, day mode, logbook, debrief) shares one
/// lookup without knowing where the names come from (currently the Navigraph DFD plus a
/// curated override list). Null means "no name known" — callers keep their existing spelled/
/// raw-ICAO fallback, so a missing DFD degrades, never fails.
/// </summary>
public interface IAirportNames
{
    /// <summary>The spoken name for an ICAO ident, or null when none is known (blank input,
    /// no DFD, airport absent). Must never throw.</summary>
    string? SpokenName(string? icao);
}
