using ProsimCompanion.Core.Configuration;
using Xunit;

namespace ProsimCompanion.Core.Tests.Configuration;

/// <summary>
/// The Approach Gates card (Settings → Voice First Officer → Callouts &amp; Placards) lets the
/// pilot move, add and remove gates. The stabilized-approach monitor latches each fired gate
/// by NAME, so the names must stay unique and follow the heights.
/// </summary>
public sealed class SopApproachGateNameTests
{
    [Fact]
    public void NumericName_FollowsTheEditedHeight()
    {
        var gates = new List<ApproachGate> { new() { Name = "1000", AglFt = 1500 }, new() { Name = "500", AglFt = 500 } };

        SopOptions.SyncApproachGateNames(gates);

        Assert.Equal(["1500", "500"], gates.Select(g => g.Name));
    }

    [Fact]
    public void BlankName_TakesTheHeight()
    {
        var gates = new List<ApproachGate> { new() { Name = "", AglFt = 1500 }, new() { Name = "  ", AglFt = 300 } };

        SopOptions.SyncApproachGateNames(gates);

        Assert.Equal(["1500", "300"], gates.Select(g => g.Name));
    }

    [Fact]
    public void HandWrittenName_IsKept()
    {
        var gates = new List<ApproachGate> { new() { Name = "1000 IMC", AglFt = 1200 } };

        SopOptions.SyncApproachGateNames(gates);

        Assert.Equal("1000 IMC", gates[0].Name);
    }

    [Fact]
    public void TwoGatesAtOneHeight_GetDistinctNames()
    {
        // One name for two gates would let the first latch silence the second.
        var gates = new List<ApproachGate>
        {
            new() { Name = "1000", AglFt = 1000 },
            new() { Name = "500", AglFt = 1000 },
            new() { Name = "", AglFt = 1000 },
        };

        SopOptions.SyncApproachGateNames(gates);

        Assert.Equal(["1000", "1000-2", "1000-3"], gates.Select(g => g.Name));
    }

    [Fact]
    public void DefaultGates_AreAlreadyInStep()
    {
        var gates = SopOptions.DefaultApproachGates.ToList();

        SopOptions.SyncApproachGateNames(gates);

        Assert.Equal(["1000", "500"], gates.Select(g => g.Name));
    }
}
