namespace ProsimCompanion.Core.Profiles;

/// <summary>Pure profile-matching logic: exact title matches beat substring matches; within the
/// same match type, list order decides (first wins).</summary>
public static class AircraftProfileMatcher
{
    public static AircraftProfile? Match(IReadOnlyList<AircraftProfile> profiles, string? aircraftTitle)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        if (string.IsNullOrWhiteSpace(aircraftTitle))
        {
            return null;
        }

        AircraftProfile? containsMatch = null;
        foreach (var profile in profiles)
        {
            if (string.IsNullOrWhiteSpace(profile.MatchString))
            {
                continue;
            }

            switch (profile.MatchType)
            {
                case ProfileMatchType.TitleEquals
                    when aircraftTitle.Equals(profile.MatchString, StringComparison.OrdinalIgnoreCase):
                    return profile;

                case ProfileMatchType.TitleContains
                    when containsMatch is null
                        && aircraftTitle.Contains(profile.MatchString, StringComparison.OrdinalIgnoreCase):
                    containsMatch = profile;
                    break;
            }
        }

        return containsMatch;
    }
}
