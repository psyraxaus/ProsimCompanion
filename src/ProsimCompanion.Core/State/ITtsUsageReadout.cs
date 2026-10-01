namespace ProsimCompanion.Core.State;

/// <summary>
/// Read-only view of the paid-TTS monthly character counters for the settings pages. Kept
/// in Core so the Web project (which references only Core) can inject it; the speech pillar
/// implements it over its usage tracker and cache root.
/// </summary>
public interface ITtsUsageReadout
{
    /// <summary>Characters billed this UTC month by the named provider ("google",
    /// "elevenlabs"); 0 when nothing has been counted.</summary>
    int CharactersThisMonth(string provider);
}
