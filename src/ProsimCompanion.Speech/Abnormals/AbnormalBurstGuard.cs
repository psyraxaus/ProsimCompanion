namespace ProsimCompanion.Speech.Abnormals;

/// <summary>
/// The burst guard (issue #123, owner report 2026-11-01): a ProSim configuration load, or the
/// overhead fire test, flips many systems at once on the ground with the engines off — on
/// 2026-11-01 ENG 1/2 FIRE, APU FIRE, APU GEN FAULT and IR 1/2/3 FAULT all rose within two
/// seconds and all cleared seven seconds later, and the FO ran the fire drill, spoke seven
/// ECAM lines and offered seven tech-log items for faults that never were. A burst is held:
/// no trigger fires until the burst has persisted for <see cref="HoldSeconds"/>; a burst that
/// clears inside the hold was never a fault. Airborne, or with an engine running, nothing is
/// held — a real multi-system failure in flight must still be called at once. Pure.
/// </summary>
public static class AbnormalBurstGuard
{
    /// <summary>Triggers rising within this window of each other count as one burst.</summary>
    public const double BurstWindowSeconds = 2;

    /// <summary>This many rising together on the ground, engines off, is a burst — or two
    /// fire loops, which never trip together outside a test.</summary>
    public const int BurstCount = 3;

    /// <summary>How long a burst must persist before its triggers may fire.</summary>
    public const double HoldSeconds = 10;

    /// <summary>The hold deadline implied by the rising edges, or null when there is no burst
    /// (or the aircraft is not parked with the engines off). The deadline is the burst's
    /// earliest rise plus <see cref="HoldSeconds"/>, so a burst that has already persisted
    /// that long is not held any more.</summary>
    public static DateTimeOffset? HoldUntil(IEnumerable<(string Id, DateTimeOffset? RisingSinceUtc)> triggers, bool onGround, bool anyEngineRunning)
    {
        ArgumentNullException.ThrowIfNull(triggers);
        if (!onGround || anyEngineRunning)
        {
            return null;
        }

        var rising = triggers.Where(t => t.RisingSinceUtc is not null).OrderBy(t => t.RisingSinceUtc).ToList();
        if (rising.Count == 0)
        {
            return null;
        }

        // Walk the rises in time order; a window that holds a burst-sized group, or two fire
        // loops, decides.
        for (var i = 0; i < rising.Count; i++)
        {
            var start = rising[i].RisingSinceUtc!.Value;
            var group = rising.Skip(i).TakeWhile(t => (t.RisingSinceUtc!.Value - start).TotalSeconds <= BurstWindowSeconds).ToList();
            var fires = group.Count(t => t.Id.Contains("fire", StringComparison.OrdinalIgnoreCase));
            if (group.Count >= BurstCount || fires >= 2)
            {
                return start.AddSeconds(HoldSeconds);
            }
        }

        return null;
    }
}
