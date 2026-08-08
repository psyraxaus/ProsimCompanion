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
    [InlineData("aircraft.communication.windows.vhf1")]   // native-audio guard (Phase 4 review find)
    [InlineData("aircraft.communication.windows.cab")]
    [InlineData("system.analog.A_FC_FO_ROLL")]             // FO sweep side of the flight-control check
    [InlineData("system.analog.A_FC_FO_PITCH")]
    [InlineData("system.analog.A_FC_FO_RUDDER")]
    // Captain-side analogs became gate-allowed with the pilot-seat setting (#24): when the
    // human flies from the RIGHT seat, the virtual pilot sweeps the left-side controls.
    // The seat-relative "never move the HUMAN's controls" rule is enforced in
    // ControlSweepService, which only maps to this side when speech.pilotSeat = "right".
    [InlineData("system.analog.A_FC_CAPT_ROLL")]
    [InlineData("system.analog.A_FC_CAPT_PITCH")]
    [InlineData("system.analog.A_FC_CAPT_RUDDER")]
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
