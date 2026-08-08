using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Core.Profiles;

/// <summary>
/// One aircraft profile: matched against the simulator aircraft title
/// (<c>simulator.aircraft.title</c>) or the flight's airline callsign prefix, it carries
/// per-aircraft feature settings. The GSX block is the first per-profile section (the
/// Prosim2GSX model — each airline/livery keeps its own ground-ops configuration).
/// </summary>
public sealed class AircraftProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Display name, e.g. "FBW A320neo British Airways".</summary>
    public string Name { get; set; } = "";

    public ProfileMatchType MatchType { get; set; } = ProfileMatchType.TitleContains;

    /// <summary>The string compared per <see cref="MatchType"/>: an aircraft-title fragment
    /// for the title types, an ICAO airline prefix (e.g. "BAW") for Airline, unused for
    /// Default.</summary>
    public string MatchString { get; set; } = "";

    /// <summary>This profile's GSX settings block. Null = the profile does not carry GSX
    /// settings yet; it captures the current global block the first time it is edited on the
    /// GSX Settings page. When the profile activates, this block is written over the live
    /// <c>gsx</c> section (Prosim2GSX's per-profile model) — see ProfileGsxApplier.</summary>
    public GsxOptions? GsxSettings { get; set; }
}

public enum ProfileMatchType
{
    /// <summary>Case-insensitive substring match against the aircraft title.</summary>
    TitleContains,

    /// <summary>Case-insensitive exact match against the aircraft title.</summary>
    TitleEquals,

    /// <summary>The flight's ICAO airline (OFP callsign prefix) starts with the match string —
    /// Prosim2GSX's Airline match, useful when one livery flies for several virtual airlines.</summary>
    Airline,

    /// <summary>Always matches, at the lowest priority — the Prosim2GSX fallback profile.</summary>
    Default,
}

/// <summary>Settings section holding all aircraft profiles (<c>aircraftProfiles</c> in config).</summary>
public sealed class AircraftProfilesOptions
{
    public const string SectionName = "aircraftProfiles";

    public List<AircraftProfile> Profiles { get; set; } = [];
}
