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
    // ProSim setup check (2026-10-06): the five IOS options "Apply recommended" may set.
    [InlineData("system.config.Config.DOORS")]
    [InlineData("system.config.Config.GROUNDPOWER")]
    [InlineData("system.config.Datalink.loadCargo")]
    [InlineData("system.config.Datalink.loadFuel")]
    [InlineData("system.config.Config.refuelRate")]
    // Cabin-call auto-answer (#11): the CAB reception latch on the captain's and the FO's
    // panel only (seat-relative pick), never the observer's or any other ACP key.
    [InlineData("system.switches.S_ASP_CAB_REC_LATCH")]
    [InlineData("system.switches.S_ASP2_CAB_REC_LATCH")]
    public void IsAllowed_AllowListedNames_ReturnTrue(string name)
        => Assert.True(ProsimWriteGate.IsAllowed(name));

    [Theory]
    [InlineData("system.switches.S_MIP_PARKING_BRAKE")]   // cockpit switch — dataref-first rule
    [InlineData("efb.simbrief.id")]                        // identity, read-only for us
    [InlineData("aircraft.speed.ias")]                     // flight dynamics are never written
    [InlineData("system.config.Config.EPR")]               // IOS engine type: same family as the setup check, never written
    [InlineData("system.config.cockpitSetup.load")]        // IOS cockpit-setup loader: same family, never written
    [InlineData("system.switches.S_ASP3_CAB_REC_LATCH")]   // observer ACP: not the FO's panel on either seat (#11)
    [InlineData("system.switches.S_ASP2_CAB_REC")]         // the momentary CAB key: the latch is written, never the press (#11)
    [InlineData("system.switches.S_ASP2_CAB_SEND")]        // the predecessor's CAB transmit press: changes the transmit channel (#11)
    [InlineData("system.switches.S_ASP2_RESET")]           // ACP RESET: never pressed by the app (#11)
    [InlineData("system.switches.S_ASP2_INT_REC_LATCH")]   // any other reception latch (#11)
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
