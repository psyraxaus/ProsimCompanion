namespace ProsimCompanion.Core.Persona;

/// <summary>
/// The pure draw (issue #194): a random FO from the pool, never the one drawn last time when
/// the pool holds more than one. A pool of one returns that one (the exclusion falls back to
/// the full pool); an empty pool returns null. The dice is passed in so tests are exact.
/// </summary>
public static class FoRosterDraw
{
    /// <param name="pool">The roster as loaded.</param>
    /// <param name="previousId">The FO to avoid (the last draw); null for none.</param>
    /// <param name="next">The dice: a non-negative integer below the given bound.</param>
    public static FoRosterEntry? Pick(IReadOnlyList<FoRosterEntry> pool, string? previousId, Func<int, int> next)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(next);
        if (pool.Count == 0)
        {
            return null;
        }

        var candidates = pool.Count > 1 && previousId is not null
            ? pool.Where(e => !string.Equals(e.Id, previousId, StringComparison.OrdinalIgnoreCase)).ToList()
            : pool.ToList();
        if (candidates.Count == 0)
        {
            candidates = pool.ToList(); // every entry shares the excluded id — only possible with a broken file
        }

        var index = next(candidates.Count);
        return candidates[Math.Clamp(index, 0, candidates.Count - 1)];
    }
}
