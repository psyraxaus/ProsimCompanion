using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Aircraft.Ofp;
using Xunit;

namespace ProsimCompanion.Core.Tests.Aircraft;

/// <summary>The one block-fuel precedence rule (2026-09-19): INIT override → OFP → EFB
/// planned fuel, each rounded up to the next 100 kg.</summary>
public sealed class EffectiveBlockFuelTests
{
    private static Dictionary<string, double> Override(double kg)
        => new(StringComparer.OrdinalIgnoreCase) { [IEfbInitOverrides.FuelRampKg] = kg };

    private static OfpData Ofp(double blockKg) => new() { RequestId = "r", FuelPlanRampKg = blockKg };

    [Fact]
    public void Override_WinsOverOfpAndEfb()
    {
        var figure = EffectiveBlockFuel.Resolve(Override(7850), Ofp(7000), 6000);

        Assert.Equal(BlockFuelSource.Override, figure.Source);
        Assert.Equal(7900, figure.Kg); // rounded up to the fuel-order increment
    }

    [Fact]
    public void Ofp_WinsOverEfb_WhenNoOverride()
    {
        var figure = EffectiveBlockFuel.Resolve(new Dictionary<string, double>(), Ofp(7000), 6000);

        Assert.Equal(BlockFuelSource.Ofp, figure.Source);
        Assert.Equal(7000, figure.Kg);
    }

    [Fact]
    public void Efb_StandsIn_WhenNoOfp()
    {
        var figure = EffectiveBlockFuel.Resolve(null, null, 6420);

        Assert.Equal(BlockFuelSource.EfbPlannedFuel, figure.Source);
        Assert.Equal(6500, figure.Kg);
    }

    [Fact]
    public void NothingAvailable_IsNone()
    {
        var figure = EffectiveBlockFuel.Resolve(null, null, 0);

        Assert.Equal(BlockFuelSource.None, figure.Source);
        Assert.False(figure.HasValue);
    }

    [Fact]
    public void OtherOverrides_DoNotCount()
    {
        var overrides = new Dictionary<string, double> { [IEfbInitOverrides.ZfwKg] = 60000 };

        var figure = EffectiveBlockFuel.Resolve(overrides, Ofp(7000), 0);

        Assert.Equal(BlockFuelSource.Ofp, figure.Source);
    }

    [Fact]
    public void PlanKg_OverrideElseOfp_NeverEfb()
    {
        Assert.Equal(8000, EffectiveBlockFuel.PlanKg(Override(8000), Ofp(7000)));
        Assert.Equal(7000, EffectiveBlockFuel.PlanKg(null, Ofp(7000)));
        Assert.Equal(0, EffectiveBlockFuel.PlanKg(null, null));
    }
}
