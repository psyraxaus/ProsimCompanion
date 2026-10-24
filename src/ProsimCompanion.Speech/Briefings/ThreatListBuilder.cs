using ProsimCompanion.Core.Weather;

namespace ProsimCompanion.Speech.Briefings;

/// <summary>
/// The threats of the day for the takeoff briefing (issue #180), composed from the facts the
/// briefing already holds: the runway surface and thunderstorms from the METAR, low visibility
/// or ceiling, ground icing, the open tech-log items and terrain (an MSA well above the
/// airport). Pure, so the clause tables pin every item; the wind-limit items arrive with
/// package 3 of the November 2026 batch and slot in here. Order is fixed: the runway first,
/// the weather, the aircraft, the terrain last.
/// </summary>
public static class ThreatListBuilder
{
    /// <summary>Visibility below which the day counts as low visibility (metres).</summary>
    public const int LowVisibilityM = 1500;

    /// <summary>Ceiling below which the day counts as low cloud (feet).</summary>
    public const int LowCeilingFt = 1000;

    /// <summary>MSA this far above the airport elevation names terrain as a threat (feet).</summary>
    public const int TerrainMsaMarginFt = 5000;

    /// <summary>The threat items in spoken form, empty when there are none.</summary>
    public static IReadOnlyList<string> Build(BriefingFacts facts, EmergencyBriefFacts emergency)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(emergency);
        var items = new List<string>();

        switch (emergency.Surface)
        {
            case RunwaySurfaceState.Wet:
                items.Add("wet runway");
                break;
            case RunwaySurfaceState.Contaminated:
                items.Add("contaminated runway");
                break;
        }

        if (emergency.Thunderstorms)
        {
            items.Add("thunderstorms in the vicinity");
        }

        if (facts.VisibilityM is { } visibility && visibility < LowVisibilityM)
        {
            items.Add($"low visibility, {visibility} metres");
        }

        if (emergency.CeilingFt is { } ceiling && ceiling < LowCeilingFt)
        {
            items.Add($"low cloud, ceiling {ceiling} feet");
        }

        if (emergency.IcingLikely)
        {
            items.Add("icing conditions, engine anti-ice");
        }

        if (facts.TechLogItems is { Count: > 0 } techLog)
        {
            items.Add(techLog.Count == 1 ? "one open tech log item" : $"{techLog.Count} open tech log items");
        }

        if (IsTerrainThreat(emergency))
        {
            items.Add($"terrain, minimum safe altitude {emergency.MsaFt!.Value:0} feet");
        }

        return items;
    }

    /// <summary>Every figure the threat list may speak, for the allowed-number set.</summary>
    public static IEnumerable<double> Numbers(BriefingFacts facts, EmergencyBriefFacts emergency)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(emergency);
        if (facts.VisibilityM is { } visibility)
        {
            yield return visibility;
        }

        if (emergency.CeilingFt is { } ceiling)
        {
            yield return ceiling;
        }

        if (facts.TechLogItems is { Count: > 0 } techLog)
        {
            yield return techLog.Count;
        }

        if (emergency.MsaFt is { } msa)
        {
            yield return Math.Round(msa);
        }
    }

    /// <summary>True when the MSA sits more than <see cref="TerrainMsaMarginFt"/> above the
    /// airport — both figures must be known.</summary>
    public static bool IsTerrainThreat(EmergencyBriefFacts emergency)
    {
        ArgumentNullException.ThrowIfNull(emergency);
        return emergency.MsaFt is { } msa && emergency.AirportElevationFt is { } elevation
            && msa > elevation + TerrainMsaMarginFt;
    }
}
