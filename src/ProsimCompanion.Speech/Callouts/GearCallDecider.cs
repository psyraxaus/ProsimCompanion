using System.Globalization;
using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Speech.Callouts;

/// <summary>
/// The pure speed check behind <see cref="GearCallFeature"/>: VLO extension for "gear
/// down", VLO retraction for "gear up" (owner's A322 figures 2026-09-20: 250 / 220 kt).
/// Kept free of the dataref write and the arbiter so the refusal wording and the boundary
/// are testable without a sim.
/// </summary>
public static class GearCallDecider
{
    /// <summary>The PM's refusal when the call would move the lever above its limit, or null
    /// when the speed is acceptable (or the limit is disabled with 0). At the limit is not an
    /// exceedance — the same inclusive comparison the flap placard check uses.</summary>
    public static string? SpeedRefusal(bool up, double indicatedAirspeedKt, SopOptions sop)
    {
        ArgumentNullException.ThrowIfNull(sop);

        var limit = up ? sop.GearRetractMaxKt : sop.GearExtendMaxKt;
        if (limit <= 0 || indicatedAirspeedKt <= limit)
        {
            return null;
        }

        var ias = ((int)Math.Round(indicatedAirspeedKt)).ToString(CultureInfo.InvariantCulture);
        var limitText = limit.ToString(CultureInfo.InvariantCulture);
        return up
            ? $"Negative — speed {ias}, gear retraction limit is {limitText}."
            : $"Negative — speed {ias}, gear extension limit is {limitText}.";
    }
}
