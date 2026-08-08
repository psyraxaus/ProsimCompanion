namespace ProsimCompanion.Core.Profiles;

/// <summary>Pure profile-matching core. Prosim2GSX's priority order: exact title, then title
/// substring, then airline prefix, then the Default fallback profile. Title matches need a
/// known aircraft title; airline matches need a known airline; Default always applies.</summary>
public static class AircraftProfileMatcher
{
    public static AircraftProfile? Match(
        IReadOnlyList<AircraftProfile> profiles,
        string? aircraftTitle,
        string? airlineIcao = null)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        AircraftProfile? containsMatch = null;
        AircraftProfile? airlineMatch = null;
        AircraftProfile? defaultMatch = null;

        foreach (var profile in profiles)
        {
            switch (profile.MatchType)
            {
                case ProfileMatchType.TitleEquals
                    when !string.IsNullOrWhiteSpace(profile.MatchString)
                        && aircraftTitle?.Equals(profile.MatchString, StringComparison.OrdinalIgnoreCase) == true:
                    return profile;

                case ProfileMatchType.TitleContains
                    when containsMatch is null
                        && !string.IsNullOrWhiteSpace(profile.MatchString)
                        && aircraftTitle?.Contains(profile.MatchString, StringComparison.OrdinalIgnoreCase) == true:
                    containsMatch = profile;
                    break;

                case ProfileMatchType.Airline
                    when airlineMatch is null
                        && !string.IsNullOrWhiteSpace(profile.MatchString)
                        && airlineIcao?.StartsWith(profile.MatchString.Trim(), StringComparison.OrdinalIgnoreCase) == true:
                    airlineMatch = profile;
                    break;

                case ProfileMatchType.Default when defaultMatch is null:
                    defaultMatch = profile;
                    break;
            }
        }

        return containsMatch ?? airlineMatch ?? defaultMatch;
    }
}
