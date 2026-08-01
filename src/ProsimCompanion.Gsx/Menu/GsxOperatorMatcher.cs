namespace ProsimCompanion.Gsx.Menu;

/// <summary>
/// Pure operator-menu resolution: the first preference keyword (in preference order) contained
/// in any entry wins; the "[GSX choice]" floating token is always an accepted fallback. No match
/// ⇒ null — the menu is left for the user (v1's blind pick-first was deliberately abandoned).
/// </summary>
public static class GsxOperatorMatcher
{
    private const string GsxChoiceToken = "GSX choice";

    /// <summary>Returns the 0-based entry index to pick, or null when nothing matches.</summary>
    public static int? PickOperator(IReadOnlyList<string> entries, IReadOnlyList<string> preferences)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(preferences);

        foreach (var preference in preferences)
        {
            if (string.IsNullOrWhiteSpace(preference))
            {
                continue;
            }

            for (var i = 0; i < entries.Count; i++)
            {
                if (entries[i].Contains(preference, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }

        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i].Contains(GsxChoiceToken, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return null;
    }
}
