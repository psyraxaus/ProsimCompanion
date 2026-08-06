namespace ProsimCompanion.Core.Deice;

/// <summary>Precipitation condition for a holdover-time lookup — mirrors the column headings of
/// the published FAA/TC/EASA HOT guideline tables. The crew picks the condition (ProSim exposes
/// no reliable precipitation dataref), the same way a real crew reads it off the HOT card.</summary>
public enum HotPrecip
{
    None = 0,
    ActiveFrost,
    FreezingFog,
    Snow,
    FreezingDrizzleLight,
    FreezingDrizzleModerate,
    LightFreezingRain,
    RainOnColdSoakedWing,
}

/// <summary>
/// Holdover-time lookup, ported verbatim from the predecessor.
///
/// ⚠ SIM IMMERSION ONLY. The figures are representative of the STRUCTURE of the published FAA
/// Holdover Time Guidelines (fluid type × concentration × OAT band × precipitation), rounded
/// into plausible ranges. They are NOT a certified HOT table and MUST NOT be used for
/// real-world dispatch or flight planning.
///
/// Returns the (low, high) holdover window in minutes, or null when no holdover applies (no
/// precipitation, or OAT below the fluid family's lowest operational use temperature).
/// </summary>
public static class HotMatrix
{
    public readonly record struct HotWindow(double LowMinutes, double HighMinutes);

    // OAT bands: 0: ≥ −3 · 1: −3 > OAT ≥ −14 · 2: −14 > OAT ≥ −25 · 3: < −25 (below LOUT).
    private static int GetBand(double oatC)
    {
        if (oatC >= -3)
        {
            return 0;
        }
        if (oatC >= -14)
        {
            return 1;
        }
        return oatC >= -25 ? 2 : 3;
    }

    /// <param name="fluidType">1=Type I, 2=Type II, 3=Type III, 4=Type IV (the GSX enum).</param>
    /// <param name="concentration">100 / 75 / 50 percent fluid; ignored for Type I (heated,
    /// single-table).</param>
    public static HotWindow? Lookup(int fluidType, int concentration, double oatC, HotPrecip precip)
    {
        if (precip == HotPrecip.None)
        {
            return null;
        }
        var band = GetBand(oatC);

        // Active frost is independent of OAT band and fluid potency.
        if (precip == HotPrecip.ActiveFrost)
        {
            return fluidType == 1 ? new HotWindow(45, 45) : new HotWindow(480, 480);
        }

        if (fluidType == 1)
        {
            if (band == 3)
            {
                return null; // below Type I generic LOUT
            }
            return precip switch
            {
                HotPrecip.FreezingFog => Band(band, (11, 17), (8, 13), (5, 9)),
                HotPrecip.Snow => Band(band, (6, 11), (4, 6), (4, 6)),
                HotPrecip.FreezingDrizzleLight => Band(band, (5, 9), (2, 5), null),
                HotPrecip.FreezingDrizzleModerate => Band(band, (4, 7), (2, 4), null),
                HotPrecip.LightFreezingRain => Band(band, (3, 6), (2, 5), null),
                HotPrecip.RainOnColdSoakedWing => Band(band, (2, 5), null, null),
                _ => null,
            };
        }

        // Thickened fluids (Type II/III/IV). Type IV is the reference; II/III scale down.
        if (band == 3)
        {
            return null;
        }

        var baseWindow = precip switch
        {
            HotPrecip.FreezingFog => Band(band, (75, 160), (45, 110), (20, 55)),
            HotPrecip.Snow => Band(band, (35, 75), (20, 45), (15, 40)),
            HotPrecip.FreezingDrizzleLight => Band(band, (50, 110), (25, 55), null),
            HotPrecip.FreezingDrizzleModerate => Band(band, (20, 45), (10, 25), null),
            HotPrecip.LightFreezingRain => Band(band, (25, 55), (10, 30), null),
            HotPrecip.RainOnColdSoakedWing => Band(band, (20, 45), null, null),
            _ => null,
        };
        if (baseWindow is null)
        {
            return null;
        }

        // Family potency relative to Type IV @ 100; concentration 100→1.0, 75→0.6, 50→0.35.
        var family = fluidType == 4 ? 1.0 : fluidType == 2 ? 0.7 : 0.6;
        var concentrationFactor = concentration >= 100 ? 1.0 : concentration >= 75 ? 0.6 : 0.35;
        var factor = family * concentrationFactor;

        var window = baseWindow.Value;
        return new HotWindow(
            Math.Round(window.LowMinutes * factor),
            Math.Round(window.HighMinutes * factor));
    }

    private static HotWindow? Band(
        int band,
        (double Lo, double Hi)? band0,
        (double Lo, double Hi)? band1,
        (double Lo, double Hi)? band2)
    {
        var selected = band switch { 0 => band0, 1 => band1, _ => band2 };
        return selected is null ? null : new HotWindow(selected.Value.Lo, selected.Value.Hi);
    }
}
