using ProsimCompanion.Core.Aircraft;
using Xunit;

namespace ProsimCompanion.Core.Tests.Aircraft;

public sealed class LoadMathTests
{
    /// <summary>The A322-style four-zone cabin from the predecessors' fallback capacities.</summary>
    private static readonly int[] Capacities = [24, 30, 36, 42];

    [Fact]
    public void DistributePax_PartialLoad_FillsEveryZoneToTheSameLoadFactor()
    {
        // The owner-reported realism bug: 99 of 132 pax must NOT front-fill (CG shift).
        var zones = LoadMath.DistributePax(99, Capacities);

        Assert.Equal(99, zones.Sum());
        for (var i = 0; i < zones.Length; i++)
        {
            var loadFactor = (double)zones[i] / Capacities[i];
            Assert.InRange(loadFactor, 0.70, 0.80); // ~75% everywhere, front to back
        }
    }

    [Fact]
    public void DistributePax_FullLoad_MatchesCapacities()
        => Assert.Equal(Capacities, LoadMath.DistributePax(132, Capacities));

    [Fact]
    public void DistributePax_SumAlwaysExact_ForEveryLoad()
    {
        for (var pax = 0; pax <= 132; pax++)
        {
            var zones = LoadMath.DistributePax(pax, Capacities);
            Assert.Equal(pax, zones.Sum());
            for (var i = 0; i < zones.Length; i++)
            {
                Assert.InRange(zones[i], 0, Capacities[i]);
            }
        }
    }

    [Fact]
    public void DistributePax_OverCapacity_ClampsToTotalCapacity()
        => Assert.Equal(132, LoadMath.DistributePax(200, Capacities).Sum());

    [Fact]
    public void DistributePax_NoCapacityData_ReturnsZeros()
        => Assert.All(LoadMath.DistributePax(99, [0, 0, 0, 0]), zone => Assert.Equal(0, zone));

    [Fact]
    public void SplitCargo_ProportionalToCapacity()
    {
        var (forward, aft) = LoadMath.SplitCargo(3000, 3402, 6033);

        Assert.Equal(3000, forward + aft, precision: 3);
        Assert.True(aft > forward);
        Assert.Equal(3000.0 * 3402 / (3402 + 6033), forward, precision: 3);
    }

    [Fact]
    public void SplitCargo_NoCapacityData_EverythingAft()
        => Assert.Equal((0, 1500), LoadMath.SplitCargo(1500, 0, 0));

    [Theory]
    [InlineData(1000, 5000, 25, 1025)]     // filling
    [InlineData(4990, 5000, 25, 5000)]     // clamps to target, never overshoots
    [InlineData(6000, 5000, 25, 5975)]     // defueling works too
    [InlineData(5000, 5000, 25, 5000)]     // at target stays
    [InlineData(1000, 5000, 0, 1000)]      // zero rate is a no-op
    public void NextFuelStep_MovesTowardTargetWithoutOvershoot(double current, double target, double rate, double expected)
        => Assert.Equal(expected, LoadMath.NextFuelStep(current, target, rate), precision: 3);

    [Theory]
    [InlineData(7317, 7400)]    // SimBrief plan_ramp rounds up to the fuel-order increment
    [InlineData(7301, 7400)]
    [InlineData(7400, 7400)]    // already round — unchanged
    [InlineData(50, 100)]
    [InlineData(0, 0)]          // 0 means "no target" and must stay 0
    [InlineData(-5, -5)]        // never invents fuel from a bogus reading
    public void RoundFuelUpToHundredKg_MatchesFuelOrderIncrements(double kg, double expected)
        => Assert.Equal(expected, LoadMath.RoundFuelUpToHundredKg(kg), precision: 3);
}


