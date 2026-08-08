namespace ProsimCompanion.Core.Aircraft.WeightAndBalance;

/// <summary>
/// Weight-dependent %MAC limits for the A320 (A322) — the trim-envelope polygon the CG chart
/// artwork draws, encoded as data so the Loadsheet brackets and W&amp;B valid-range line get real
/// per-weight limits instead of the old fixed 21–38 window.
///
/// Provenance (2026-08-08): digitized from <c>wwwroot/img/wandb.png</c> (the artwork both this
/// app and Prosim2GSX render), by extracting the thick envelope strokes from the pixel data and
/// mapping them through the chart's axis calibration (%MAC 20–39 across, 78–35 t up — the same
/// mapping CgEnvelope.razor uses). Neither predecessor had polygon data; Prosim2GSX validated
/// against a flat 10.5–45 %MAC window. Values are chart-accurate to roughly ±0.15 %MAC.
///
/// Shape notes, visible in the artwork:
/// - Above MZFW (61 t) a single operational envelope applies; its forward limit has a kink at
///   ~71.8 t (20.8 %MAC) and the aft limit peaks at ~70.6 t (37.7 %MAC), both narrowing
///   steeply toward the chart top (~77 t).
/// - Below 61 t the take-off envelope is the OUTER pair (wider than the operational envelope
///   at the junction — the aft limit genuinely steps from 36.3 to 35.7 crossing 61 t upward),
///   and the ZFW envelope is the INNER pair.
/// </summary>
public static class A320TrimEnvelope
{
    /// <summary>Chart floor/ceiling in tonnes — outside this the nearest edge value applies.</summary>
    public const double MinChartTonnes = 37.4;
    public const double MaxChartTonnes = 77.0;

    // (tonnes, %MAC) polylines, ascending by weight. Gross-weight envelope = the union bound:
    // take-off (outer) below 61 t, operational above — with the aft-side step encoded as two
    // points a hair apart at the MZFW junction.
    private static readonly (double Tonnes, double Mac)[] GrossForward =
    [
        (37.4, 23.78),
        (40.0, 23.40),
        (42.2, 23.05),
        (45.0, 22.63),
        (47.8, 22.21),
        (50.2, 21.87),
        (53.4, 21.44),
        (55.0, 21.50),
        (57.8, 21.59),
        (61.0, 21.71),
        (63.0, 21.76),
        (65.4, 21.51),
        (68.2, 21.21),
        (70.2, 21.00),
        (71.8, 20.83),
        (73.0, 21.99),
        (73.8, 23.45),
        (75.0, 26.35),
        (76.2, 29.33),
        (77.0, 31.24),
    ];

    private static readonly (double Tonnes, double Mac)[] GrossAft =
    [
        (37.4, 30.96),
        (40.2, 31.60),
        (43.4, 32.33),
        (46.6, 33.07),
        (49.8, 33.80),
        (53.0, 34.53),
        (56.6, 35.36),
        (58.6, 35.81),
        (60.99, 36.35),
        (61.0, 35.65), // crossing MZFW upward the take-off envelope ends; operational is narrower
        (64.2, 36.29),
        (67.0, 36.90),
        (70.6, 37.66),
        (71.8, 37.18),
        (73.0, 36.57),
        (74.2, 35.97),
        (75.8, 35.17),
        (77.0, 34.49),
    ];

    private static readonly (double Tonnes, double Mac)[] ZfwForward =
    [
        (37.4, 24.19),
        (40.0, 23.80),
        (43.0, 23.35),
        (46.0, 22.90),
        (49.0, 22.60),
        (61.0, 22.60),
    ];

    private static readonly (double Tonnes, double Mac)[] ZfwAft =
    [
        (37.4, 28.09),
        (40.0, 28.50),
        (43.4, 29.00),
        (47.0, 29.57),
        (49.0, 30.31),
        (53.0, 32.08),
        (57.0, 33.84),
        (61.0, 35.60),
    ];

    /// <summary>Forward/aft %MAC limits for a gross weight (take-off envelope below MZFW,
    /// operational above). Weights outside the chart clamp to the nearest edge.</summary>
    public static (double MinMac, double MaxMac) GrossWeightLimits(double weightKg)
        => (Interpolate(GrossForward, weightKg / 1000.0), Interpolate(GrossAft, weightKg / 1000.0));

    /// <summary>Forward/aft %MAC limits for a zero-fuel weight (the inner ZFW-limit pair;
    /// the curve only exists up to MZFW and clamps beyond).</summary>
    public static (double MinMac, double MaxMac) ZeroFuelLimits(double weightKg)
        => (Interpolate(ZfwForward, weightKg / 1000.0), Interpolate(ZfwAft, weightKg / 1000.0));

    /// <summary>True when the CG sits inside the gross-weight envelope (inclusive).</summary>
    public static bool IsGrossCgWithinLimits(double weightKg, double cgMac)
    {
        var (min, max) = GrossWeightLimits(weightKg);
        return cgMac >= min && cgMac <= max;
    }

    /// <summary>True when the CG sits inside the ZFW envelope (inclusive).</summary>
    public static bool IsZfwCgWithinLimits(double weightKg, double cgMac)
    {
        var (min, max) = ZeroFuelLimits(weightKg);
        return cgMac >= min && cgMac <= max;
    }

    private static double Interpolate((double Tonnes, double Mac)[] table, double tonnes)
    {
        if (!double.IsFinite(tonnes) || tonnes <= table[0].Tonnes)
        {
            return table[0].Mac;
        }

        if (tonnes >= table[^1].Tonnes)
        {
            return table[^1].Mac;
        }

        for (var i = 1; i < table.Length; i++)
        {
            if (tonnes <= table[i].Tonnes)
            {
                var (t0, m0) = table[i - 1];
                var (t1, m1) = table[i];
                return m0 + ((tonnes - t0) / (t1 - t0) * (m1 - m0));
            }
        }

        return table[^1].Mac; // unreachable — the [^1] guard above covers the tail
    }
}
