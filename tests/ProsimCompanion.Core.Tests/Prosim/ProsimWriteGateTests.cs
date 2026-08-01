using ProsimCompanion.Prosim.DataRefs;
using Xunit;

namespace ProsimCompanion.Core.Tests.Prosim;

public sealed class ProsimWriteGateTests
{
    [Theory]
    [InlineData("aircraft.refuel.fuelTarget.kg")]
    [InlineData("aircraft.fuel.total.amount.kg")]
    [InlineData("efb.chocks")]
    [InlineData("aircraft.passengers.zone3.amount")]
    [InlineData("aircraft.cargo.forward.amount")]
    [InlineData("doors.entry.left.fwd")]
    [InlineData("efb.gsx.refuel")]
    public void IsAllowed_AllowListedNames_ReturnTrue(string name)
        => Assert.True(ProsimWriteGate.IsAllowed(name));

    [Theory]
    [InlineData("system.switches.S_MIP_PARKING_BRAKE")]   // cockpit switch — dataref-first rule
    [InlineData("efb.simbrief.id")]                        // identity, read-only for us
    [InlineData("aircraft.speed.ias")]                     // flight dynamics are never written
    [InlineData("")]
    public void IsAllowed_UnlistedNames_ReturnFalse(string name)
        => Assert.False(ProsimWriteGate.IsAllowed(name));

    [Fact]
    public void EnsureAllowed_UnlistedName_ThrowsWithGuidance()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => ProsimWriteGate.EnsureAllowed("aircraft.speed.ias"));

        Assert.Contains("allow-list", ex.Message, StringComparison.Ordinal);
    }
}
