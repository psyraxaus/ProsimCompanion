namespace ProsimCompanion.Core.Weather;

/// <summary>Outcome classification for one weather lookup (issue #62). The three-way split
/// exists because "the source answered and has nothing for this ICAO" and "the source could
/// not be asked" demand different pilot guidance: the first means the station genuinely has no
/// observation, the second means the fetch itself failed and retrying (or fixing a connection)
/// may help.</summary>
public enum WxProbeStatus
{
    /// <summary>A real observation was returned (<see cref="WxProbe.Facts"/> carries it).</summary>
    Found,

    /// <summary>A source was reachable and authoritatively had no observation for the ICAO.</summary>
    NoData,

    /// <summary>No source could be consulted (not installed, unreachable, HTTP error…).</summary>
    Unavailable,
}

/// <summary>
/// A weather lookup result that carries WHY it is empty. Born from the 2026-08-16 flight where
/// the gateway METAR endpoint 500'd deterministically and the perf pages showed a bare
/// "No METAR available" while two other tiers held valid observations — a bare null cannot
/// distinguish "no data for this ICAO" from "fetch failed", so the UI could not say which.
/// </summary>
public sealed record WxProbe(WxProbeStatus Status, WxFacts Facts, string? Detail)
{
    /// <summary>A successful observation (never used with an empty <see cref="WxFacts.RawMetar"/>).</summary>
    public static WxProbe Found(WxFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return new(WxProbeStatus.Found, facts, null);
    }

    /// <summary>Source reachable, no observation for the ICAO. <paramref name="detail"/> names
    /// the source(s) that said so.</summary>
    public static WxProbe NoData(string? detail) => new(WxProbeStatus.NoData, WxFacts.None, detail);

    /// <summary>Source(s) could not be consulted. <paramref name="detail"/> says why.</summary>
    public static WxProbe Unavailable(string? detail) => new(WxProbeStatus.Unavailable, WxFacts.None, detail);

    /// <summary>
    /// Human-readable failure line for the UI, or null when the probe found weather. Kept here
    /// (pure, testable) so every page renders the same wording for the same outcome.
    /// </summary>
    public string? FailureMessage(string icao)
    {
        var suffix = string.IsNullOrWhiteSpace(Detail) ? "" : $" ({Detail})";
        return Status switch
        {
            WxProbeStatus.Found => null,
            WxProbeStatus.NoData => $"No METAR for {icao}{suffix}",
            _ => $"Weather fetch failed{suffix}",
        };
    }
}
