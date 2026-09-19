using ProsimCompanion.Core.Aircraft.Ofp;

namespace ProsimCompanion.Core.Aircraft;

/// <summary>Where the effective block-fuel figure came from, for decision logs and the UI.</summary>
public enum BlockFuelSource
{
    /// <summary>No figure at all (no OFP, no INIT override, empty EFB planned fuel).</summary>
    None,

    /// <summary>The pilot's INIT-page FUEL RAMP override (real-world SOP: the crew adds to
    /// the OFP figure after their own considerations).</summary>
    Override,

    /// <summary>The imported OFP's block (ramp) fuel.</summary>
    Ofp,

    /// <summary>The EFB planned-fuel dataref — whatever the ProSim EFB fuel page holds.</summary>
    EfbPlannedFuel,
}

/// <summary>One resolved block-fuel figure: kilograms (rounded UP to the next 100 kg —
/// fuel-order increments) and its source.</summary>
public readonly record struct BlockFuelFigure(double Kg, BlockFuelSource Source)
{
    public static BlockFuelFigure None { get; } = new(0, BlockFuelSource.None);

    public bool HasValue => Kg > 0;
}

/// <summary>
/// THE block-fuel rule (2026-09-19, user report via Prosim2GSX): the INIT page's FUEL RAMP
/// override used to write only the FMS block field while the GSX refuel, the tankering
/// check, the loadsheet and the Fuel page all kept reading the OFP — the truck fueled the
/// OFP figure no matter what the pilot entered. Every consumer of "what fuel do we want on
/// board" resolves through here: override first, then the OFP block fuel, then the EFB
/// planned-fuel dataref. Pure and dependency-free so the precedence is table-testable.
/// </summary>
public static class EffectiveBlockFuel
{
    /// <summary>Resolves the figure. <paramref name="overrides"/> is the INIT override
    /// snapshot (<see cref="IEfbInitOverrides.Snapshot"/>), <paramref name="ofp"/> the
    /// current OFP (null when none), <paramref name="plannedFuelRaw"/> the cached
    /// <c>efb.plannedfuel</c> value.</summary>
    public static BlockFuelFigure Resolve(
        IReadOnlyDictionary<string, double>? overrides,
        OfpData? ofp,
        double plannedFuelRaw)
    {
        if (overrides is not null
            && overrides.TryGetValue(IEfbInitOverrides.FuelRampKg, out var overrideKg)
            && overrideKg > 0)
        {
            return new(LoadMath.RoundFuelUpToHundredKg(overrideKg), BlockFuelSource.Override);
        }

        if (ofp is { FuelPlanRampKg: > 0 })
        {
            return new(LoadMath.RoundFuelUpToHundredKg(ofp.FuelPlanRampKg), BlockFuelSource.Ofp);
        }

        if (plannedFuelRaw > 0)
        {
            return new(LoadMath.RoundFuelUpToHundredKg(plannedFuelRaw), BlockFuelSource.EfbPlannedFuel);
        }

        return BlockFuelFigure.None;
    }

    /// <summary>The figure the tankering pre-check and the loadsheet call "the plan": the
    /// override when set, else the OFP block fuel, else 0 (the EFB dataref is handled by the
    /// callers' own settle rules).</summary>
    public static double PlanKg(IReadOnlyDictionary<string, double>? overrides, OfpData? ofp)
    {
        if (overrides is not null
            && overrides.TryGetValue(IEfbInitOverrides.FuelRampKg, out var overrideKg)
            && overrideKg > 0)
        {
            return LoadMath.RoundFuelUpToHundredKg(overrideKg);
        }

        return ofp?.FuelPlanRampKg ?? 0;
    }
}
