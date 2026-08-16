using ProsimCompanion.Speech.Crew;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

public sealed class AcpHailGateCoreTests
{
    [Theory]
    [InlineData(AcpTransmitTarget.None)]
    [InlineData(AcpTransmitTarget.Vhf1)]
    [InlineData(AcpTransmitTarget.Cabin)]
    [InlineData(AcpTransmitTarget.Unknown)]
    public void GatingDisabled_AlwaysAccepts(AcpTransmitTarget target)
    {
        var core = new AcpHailGateCore();

        Assert.Equal(
            HailGateDecision.Accept,
            core.EvaluateHail(
                gatingEnabled: false, intKeyRequired: true,
                target, intKeyPushed: false, AcpTransmitTarget.Intercom));
    }

    [Fact]
    public void UnknownState_Accepts_FeatureUnavailableBehavesAsToday()
    {
        var core = new AcpHailGateCore();

        Assert.Equal(
            HailGateDecision.Accept,
            core.EvaluateHail(
                gatingEnabled: true, intKeyRequired: false,
                AcpTransmitTarget.Unknown, intKeyPushed: false, AcpTransmitTarget.Intercom));
    }

    [Fact]
    public void IntSelected_GroundHailAccepted()
    {
        var core = new AcpHailGateCore();

        Assert.Equal(
            HailGateDecision.Accept,
            core.EvaluateHail(
                gatingEnabled: true, intKeyRequired: false,
                AcpTransmitTarget.Intercom, intKeyPushed: false, AcpTransmitTarget.Intercom));
    }

    [Fact]
    public void CabSelected_CabinHailAccepted()
    {
        var core = new AcpHailGateCore();

        Assert.Equal(
            HailGateDecision.Accept,
            core.EvaluateHail(
                gatingEnabled: true, intKeyRequired: false,
                AcpTransmitTarget.Cabin, intKeyPushed: false, AcpTransmitTarget.Cabin));
    }

    [Theory]
    [InlineData(AcpTransmitTarget.None)]
    [InlineData(AcpTransmitTarget.Vhf1)]
    [InlineData(AcpTransmitTarget.Cabin)] // CAB selected but the GROUND channel is required
    public void WrongSelector_CoachesFirst_ThenRejectsSilently(AcpTransmitTarget target)
    {
        var core = new AcpHailGateCore();

        Assert.Equal(
            HailGateDecision.Coach,
            core.EvaluateHail(true, false, target, false, AcpTransmitTarget.Intercom));
        Assert.Equal(
            HailGateDecision.Reject,
            core.EvaluateHail(true, false, target, false, AcpTransmitTarget.Intercom));
        Assert.Equal(
            HailGateDecision.Reject,
            core.EvaluateHail(true, false, target, false, AcpTransmitTarget.Intercom));
    }

    [Fact]
    public void CoachingIsSpentAcrossChannels_OncePerSession()
    {
        var core = new AcpHailGateCore();

        Assert.Equal(
            HailGateDecision.Coach,
            core.EvaluateHail(true, false, AcpTransmitTarget.None, false, AcpTransmitTarget.Intercom));
        // A later cabin-channel miss does NOT coach again — one lesson per session.
        Assert.Equal(
            HailGateDecision.Reject,
            core.EvaluateHail(true, false, AcpTransmitTarget.None, false, AcpTransmitTarget.Cabin));
    }

    [Fact]
    public void IntKeyRequired_GroundHailNeedsTheKey()
    {
        var core = new AcpHailGateCore();

        Assert.Equal(
            HailGateDecision.Coach,
            core.EvaluateHail(true, true, AcpTransmitTarget.Intercom, false, AcpTransmitTarget.Intercom));
        Assert.Equal(
            HailGateDecision.Accept,
            core.EvaluateHail(true, true, AcpTransmitTarget.Intercom, true, AcpTransmitTarget.Intercom));
    }

    [Fact]
    public void IntKeyRequired_DoesNotApplyToTheCabChannel()
    {
        var core = new AcpHailGateCore();

        // S_ASP_INT_SEND is the INT transmit key; CAB has no equivalent dataref.
        Assert.Equal(
            HailGateDecision.Accept,
            core.EvaluateHail(true, true, AcpTransmitTarget.Cabin, false, AcpTransmitTarget.Cabin));
    }

    [Theory]
    [InlineData(true, AcpTransmitTarget.Vhf1, AcpTransmitTarget.Intercom, true)]
    [InlineData(true, AcpTransmitTarget.None, AcpTransmitTarget.Intercom, true)]
    [InlineData(true, AcpTransmitTarget.Intercom, AcpTransmitTarget.Intercom, false)]
    [InlineData(true, AcpTransmitTarget.Cabin, AcpTransmitTarget.Cabin, false)]
    // Unknown never hangs up — a dropped connection must not slam the phone down.
    [InlineData(true, AcpTransmitTarget.Unknown, AcpTransmitTarget.Intercom, false)]
    [InlineData(false, AcpTransmitTarget.Vhf1, AcpTransmitTarget.Intercom, false)]
    public void ShouldHangUp_OnlyWhenGatedAndSelectorLeftTheChannel(
        bool gatingEnabled, AcpTransmitTarget target, AcpTransmitTarget required, bool expected)
    {
        Assert.Equal(expected, AcpHailGateCore.ShouldHangUp(gatingEnabled, target, required));
    }
}
