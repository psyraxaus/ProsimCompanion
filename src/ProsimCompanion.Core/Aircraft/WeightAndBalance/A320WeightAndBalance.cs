namespace ProsimCompanion.Core.Aircraft.WeightAndBalance;

/// <summary>
/// A320-family weight and balance calculator — pure functions over live state + plan inputs.
/// Bit-exact port of the ProSim-internal formulas (docs/integrations/prosim.md §4): the
/// loaded-index literals carry deliberate IEEE-754 noise and the fuel-CG table is verbatim from
/// ProSim's EFB bundle — do not "clean up" either, or emitted lizfw/litow stop matching
/// ProSim's own output.
/// </summary>
public static class A320WeightAndBalance
{
    /// <summary>ProSim's internal pax mass (88 kg, not MSFS's 77) — kept for reference by
    /// future per-variant config; the headline weights flow through live datarefs.</summary>
    public const double PassengerWeightKg = 88.0;

    // Loaded-index formula constants, from ProSim's LoadsheetGenerator.CalculateLiWeights:
    //   LI = 710.4 + (mac% − 29.6) × (3.9 / 2.6)
    // The slope literals must stay exactly as decompiled (multiply first, then divide).
    private const double LiBaseIndex = 710.4;
    private const double LiReferenceMac = 29.6;
    private const double LiSlopeNumerator = 3.8999999999999773;
    private const double LiSlopeDenominator = 2.6000000000000014;

    // Plausible CG window. Real A320 CG sits ~20–40 %MAC; the generous band exists to catch
    // the unpopulated-dataref failure mode (GetValue returning 0/NaN), never to validate ops.
    public const double MinPlausibleCgMac = 5.0;
    public const double MaxPlausibleCgMac = 60.0;

    /// <summary>
    /// Rejects an implausible CG read before it can reach a loadsheet. A CG dataref that has
    /// not populated coerces to 0.0 — publishing that would put an impossible %MAC in front of
    /// the pilot, so generation aborts instead (predecessor rule, verified live).
    /// </summary>
    /// <exception cref="InvalidOperationException">The CG is outside the plausible band.</exception>
    public static double EnsurePlausibleCg(double cgMac, string label)
    {
        if (double.IsNaN(cgMac) || double.IsInfinity(cgMac)
            || cgMac < MinPlausibleCgMac || cgMac > MaxPlausibleCgMac)
        {
            throw new InvalidOperationException(
                $"Cannot generate loadsheet: {label} CG {cgMac:F2} %MAC is implausible " +
                $"(expected {MinPlausibleCgMac:F0}–{MaxPlausibleCgMac:F0} %MAC) — the dataref " +
                "likely hasn't populated.");
        }
        return cgMac;
    }

    /// <summary>
    /// Preliminary loadsheet from planned figures + live CG datarefs. The CGs come from
    /// <c>aircraft.cg</c>/<c>aircraft.zfwcg</c> (aircraft-aware in the simulator — correct for
    /// any A320-family variant; the predecessors' hardcoded 28.5 default is gone for good).
    /// At prelim time pax mass is already reflected (booked drives pax.total.weight).
    /// </summary>
    public static LoadsheetData CalculatePreliminary(FlightPlanInputs plan, WeightAndBalanceLiveState live)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(live);

        var totalCapacity = live.ZoneCapacities.Sum();

        // Deterministic capacity-proportional zone split (display values the pilot reads —
        // determinism beats variation; truncating casts, zone 4 absorbs the remainder).
        var totalPax = plan.PassengerCount;
        var loadFactor = totalCapacity > 0 ? Math.Min(1.0, (double)totalPax / totalCapacity) : 0.0;
        var zone1 = (int)(live.ZoneCapacities[0] * loadFactor);
        var zone2 = (int)(live.ZoneCapacities[1] * loadFactor);
        var zone3 = (int)(live.ZoneCapacities[2] * loadFactor);
        var zone4 = totalPax - zone1 - zone2 - zone3;

        // Cargo clamped to combined capacity, split proportionally; bulk folds into the
        // displayed aft figure (bulk has no settable dataref).
        var totalCargoCapacity = live.CargoForwardCapacityKg + live.CargoAftCapacityKg + live.CargoBulkCapacityKg;
        // Clamp to combined capacity — the caller decision-logs the clamp if it cares.
        var totalCargo = totalCargoCapacity > 0
            ? Math.Min(plan.CargoTotalKg, totalCargoCapacity)
            : plan.CargoTotalKg;
        var (forwardCargo, aftCargo) = LoadMath.SplitCargo(
            totalCargo,
            live.CargoForwardCapacityKg,
            live.CargoAftCapacityKg + live.CargoBulkCapacityKg);

        // Weights: prefer OFP estimates; fall back to live state.
        var zfw = plan.EstimatedZeroFuelWeightKg > 0 ? plan.EstimatedZeroFuelWeightKg : live.ZfwKg;
        var tow = plan.EstimatedTakeoffWeightKg > 0 ? plan.EstimatedTakeoffWeightKg : zfw + plan.PlannedFuelKg;

        var macTow = EnsurePlausibleCg(live.GrossCgMac, "PRELIM gross");
        var macZfw = EnsurePlausibleCg(live.ZfwCgMac, "PRELIM ZFW");

        var plannedTripFuel = plan.PlannedFuelKg - plan.PlannedLandingFuelKg;
        var law = plan.EstimatedLandingWeightKg > 0 ? plan.EstimatedLandingWeightKg : tow - plannedTripFuel;

        return new LoadsheetData
        {
            ZeroFuelWeight = zfw,
            ZeroFuelWeightMac = macZfw,
            TakeoffWeight = tow,
            TakeoffWeightMac = macTow,
            FuelWeight = plan.PlannedFuelKg,
            LandingWeight = law,
            LizfwIndex = Math.Round(CalculateLoadedIndex(macZfw), 2),
            LitowIndex = Math.Round(CalculateLoadedIndex(macTow), 2),
            TotalPassengers = totalPax,
            PassengersByZone = [zone1, zone2, zone3, zone4],
            ForwardCargoWeight = forwardCargo,
            AftCargoWeight = aftCargo,
        };
    }

    /// <summary>
    /// Final loadsheet entirely from live state. LandingWeight defaults to TOW here — the
    /// orchestrator overrides it with final TOW − planned trip fuel recovered from the cached
    /// prelim (trip fuel is a dispatch-time quantity, not re-measured at final).
    /// </summary>
    public static LoadsheetData CalculateFinal(WeightAndBalanceLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);

        // Belt-and-braces per-hold clamp — the simulator shouldn't over-report, but a
        // loadsheet must never print more cargo than a hold can carry.
        var forwardCargo = live.CargoForwardCapacityKg > 0
            ? Math.Min(live.CargoForwardKg, live.CargoForwardCapacityKg)
            : live.CargoForwardKg;
        var aftCargo = live.CargoAftCapacityKg > 0
            ? Math.Min(live.CargoAftKg, live.CargoAftCapacityKg)
            : live.CargoAftKg;

        var totalFuel = live.FuelCenterKg + live.FuelLeftKg + live.FuelRightKg;
        var grossCg = EnsurePlausibleCg(live.GrossCgMac, "FINAL gross");

        // ProSim's own final-generator relation: ZFW CG = gross CG − fuel-induced shift,
        // bucket lookup on floor(fuel/100), no interpolation.
        var macZfw = CalculateZfwCg((int)Math.Floor(totalFuel / 100.0), grossCg);

        return new LoadsheetData
        {
            ZeroFuelWeight = live.ZfwKg,
            ZeroFuelWeightMac = macZfw,
            TakeoffWeight = live.GrossWeightKg,
            TakeoffWeightMac = grossCg,
            FuelWeight = totalFuel,
            LandingWeight = live.GrossWeightKg,
            LizfwIndex = Math.Round(CalculateLoadedIndex(macZfw), 2),
            LitowIndex = Math.Round(CalculateLoadedIndex(grossCg), 2),
            TotalPassengers = live.ZoneAmounts.Sum(),
            PassengersByZone = (int[])live.ZoneAmounts.Clone(),
            ForwardCargoWeight = forwardCargo,
            AftCargoWeight = aftCargo,
        };
    }

    /// <summary>LI = 710.4 + (mac% − 29.6) × 3.9 / 2.6 — multiply before divide, verified
    /// anchors: macZfw 26.9 → 706.35, macTow 25.4 → 704.10 (after 2 dp rounding).</summary>
    public static double CalculateLoadedIndex(double macPercent)
        => LiBaseIndex + (macPercent - LiReferenceMac) * LiSlopeNumerator / LiSlopeDenominator;

    /// <summary>ZFW CG = gross CG − fuel-induced CG shift (table entries are negative, so the
    /// ZFW CG lands aft of the gross CG).</summary>
    public static double CalculateZfwCg(int fuelIndex, double grossCgMac)
        => grossCgMac - GetCgAdjustmentForFuel(fuelIndex);

    /// <summary>Fuel-CG correction lookup, indexed by fuel_kg / 100, clamped at both ends.</summary>
    public static double GetCgAdjustmentForFuel(int fuelIndex)
    {
        if (fuelIndex < 0)
        {
            return ZfwcgAdjArray[0];
        }
        return fuelIndex >= ZfwcgAdjArray.Length
            ? ZfwcgAdjArray[^1]
            : ZfwcgAdjArray[fuelIndex];
    }

    // ProSim's LoadsheetGenerator.ZfwcgAdjArray, verbatim from the EFB Angular bundle and
    // identical in the decompiled FinalLoadsheetGenerator. Values are %MAC shifts. The
    // duplicate-value irregularities (e.g. triples at indices 60/61/62) are in the original —
    // preserved for bit-exactness.
    private static readonly double[] ZfwcgAdjArray =
    [
        0.0, 0.0, -0.0611692667008015, -0.0611692667008015, -0.1217812299729, -0.1217812299729,
        -0.181853771209703, -0.181853771209703, -0.241386890411402, -0.241386890411402,
        -0.3003895282746, -0.3003895282746, -0.358882546424901, -0.358882546424901,
        -0.4168510437012, -0.4168510437012, -0.474315881729201, -0.474315881729201,
        -0.531265139579801, -0.531265139579801, -0.5877315998078, -0.5877315998078,
        -0.643709301948601, -0.643709301948601, -0.6992042064667, -0.6992042064667,
        -0.754219293594399, -0.754219293594399, -0.808769464492801, -0.808769464492801,
        -0.8628606796265, -0.8628606796265, -0.916483998298702, -0.916483998298702,
        -0.969660282135003, -0.969660282135003, -1.0223835706711, -1.0223835706711,
        -1.0746657848358, -1.0746657848358, -1.1265188455582, -1.1265188455582,
        -1.1779397726059, -1.1779397726059, -1.2289375066757, -1.2289375066757,
        -1.2795120477677, -1.2795120477677, -1.3296782970429, -1.3296782970429,
        -1.3794332742691, -1.3794332742691, -1.4287739992142, -1.4287739992142,
        -1.4777272939682, -1.4777272939682, -1.5262752771378, -1.5262752771378,
        -1.5744417905808, -1.5744417905808, -1.5744417905808, -1.6222298145294,
        -1.6696184873581, -1.6696184873581, -1.7166495323181, -1.7166495323181,
        -1.7632991075516, -1.7632991075516, -1.8095850944519, -1.8095850944519,
        -1.8555045127869, -1.8555045127869, -1.9010722637177, -1.9010722637177,
        -1.9462764263153, -1.9462764263153, -1.9911348819733, -1.9911348819733,
        -2.0356476306916, -2.0356476306916, -2.0798236131668, -2.0798236131668,
        -2.1236568689347, -2.1236568689347, -2.1671563386917, -2.1671563386917,
        -2.2103130817414, -2.2103130817414, -2.2531598806381, -2.2531598806381,
        -2.2956669330597, -2.2956669330597, -2.2956669330597, -2.3378670215607,
        -2.3797512054444, -2.3797512054444, -2.4213135242462, -2.4213135242462,
        -2.4625778198242, -2.4625778198242, -2.5035381317139, -2.5035381317139,
        -2.5441855192185, -2.5441855192185, -2.5845408439636, -2.5845408439636,
        -2.624598145485, -2.624598145485, -2.6643633842469, -2.6643633842469,
        -2.7038455009461, -2.7038455009461, -2.7430355548859, -2.7430355548859,
        -2.781942486763, -2.781942486763, -2.8205648064614, -2.8205648064614,
        -2.858929336071, -2.858929336071, -2.858929336071, -2.8969958424568,
        -2.9348060488701, -2.9348060488701, -2.9723450541497, -2.9723450541497,
        -2.9723450541497, -3.0413046479225, -3.1658902764321, -3.1658902764321,
        -3.2896220684052, -3.2896220684052, -3.4124657511711, -3.4124657511711,
        -3.5344690084458, -3.5344690084458, -3.6556199193001, -3.6556199193001,
        -3.7759393453598, -3.7759393453598, -3.8954108953476, -3.8954108953476,
        -4.0140777826309, -4.0140777826309, -4.1319265961647, -4.1319265961647,
        -4.2489781975746, -4.2489781975746, -4.3652310967446, -4.3652310967446,
        -4.4806927442551, -4.4806927442551, -4.5953720808029, -4.5953720808029,
        -4.7092869877816, -4.7092869877816, -4.822438955307, -4.822438955307,
        -4.9348339438439, -4.9348339438439, -4.9348339438439, -5.0464734435082,
        -5.0464734435082, -5.157370865345, -5.2675396203995, -5.2675396203995,
        -5.3769871592522, -5.3769871592522, -5.3769871592522, -5.4856985807419,
        -5.5937111377716, -5.5937111377716, -5.7010143995285, -5.7010143995285,
        -5.8076098561287, -5.8076098561287, -5.9135258197785, -5.9135258197785,
        -6.0187488794327, -6.0187488794327, -6.0187488794327, -6.123298406601,
        -6.2271744012833, -6.2271744012833, -6.3303917646408, -6.3303917646408,
        -6.4329296350479, -6.4329296350479, -6.5348356962204, -6.5348356962204,
        -6.636081635952, -6.636081635952,
    ];
}
