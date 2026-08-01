namespace ProsimCompanion.Core.Profiles;

/// <summary>
/// One aircraft profile: matched against the simulator aircraft title
/// (<c>simulator.aircraft.title</c>), it carries per-aircraft feature settings. Feature settings
/// sections are added by the pillars as they land (GSX automation first); the identity/matching
/// core lives here.
/// </summary>
public sealed class AircraftProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Display name, e.g. "FBW A320neo British Airways".</summary>
    public string Name { get; set; } = "";

    public ProfileMatchType MatchType { get; set; } = ProfileMatchType.TitleContains;

    /// <summary>The string compared against the aircraft title per <see cref="MatchType"/>.</summary>
    public string MatchString { get; set; } = "";
}

public enum ProfileMatchType
{
    /// <summary>Case-insensitive substring match against the aircraft title.</summary>
    TitleContains,

    /// <summary>Case-insensitive exact match against the aircraft title.</summary>
    TitleEquals,
}

/// <summary>Settings section holding all aircraft profiles (<c>aircraftProfiles</c> in config).</summary>
public sealed class AircraftProfilesOptions
{
    public const string SectionName = "aircraftProfiles";

    public List<AircraftProfile> Profiles { get; set; } = [];
}
