using ProsimCompanion.Audio.Acp;
using ProsimCompanion.Core.Configuration;
using Xunit;

namespace ProsimCompanion.Core.Tests.Audio;

/// <summary>
/// The per-ACP electrical gate, verbatim from the predecessor:
/// CPT (AC ESS || DC ESS) &amp;&amp; switching != 0; FO same but switching != 2; OBS on DC1 only.
/// S_AUDIO_SWITCHING: 0 = CAPT swapped to ACP3, 1 = NORM, 2 = F/O swapped to ACP3.
/// </summary>
public sealed class AcpPowerGateTests
{
    [Theory]
    [InlineData(true, false, 1, true)]   // AC ESS alone powers ACP1
    [InlineData(false, true, 1, true)]   // DC ESS alone powers ACP1
    [InlineData(false, false, 1, false)] // no essential bus ⇒ dark
    [InlineData(true, true, 0, false)]   // CAPT swapped onto ACP3 ⇒ ACP1 out of the loop
    [InlineData(true, true, 2, true)]    // F/O swap does not affect the captain
    public void Captain_GatesOnEssentialBusesAndSwitching(bool acEss, bool dcEss, int switching, bool expected)
    {
        var inputs = new AcpPowerInputs(acEss, dcEss, Dc1: false, switching);

        Assert.Equal(expected, AcpPowerGate.IsPowered(AcpSide.Captain, inputs));
    }

    [Theory]
    [InlineData(true, false, 1, true)]
    [InlineData(false, true, 1, true)]
    [InlineData(false, false, 1, false)]
    [InlineData(true, true, 2, false)]  // F/O swapped onto ACP3 ⇒ ACP2 out of the loop
    [InlineData(true, true, 0, true)]   // CAPT swap does not affect the first officer
    public void FirstOfficer_GatesOnEssentialBusesAndSwitching(bool acEss, bool dcEss, int switching, bool expected)
    {
        var inputs = new AcpPowerInputs(acEss, dcEss, Dc1: false, switching);

        Assert.Equal(expected, AcpPowerGate.IsPowered(AcpSide.FirstOfficer, inputs));
    }

    [Theory]
    [InlineData(true, 0, true)]   // DC1 powers the observer regardless of switching
    [InlineData(true, 2, true)]
    [InlineData(false, 1, false)] // essential buses do NOT power the observer panel
    public void Observer_GatesOnDc1Only(bool dc1, int switching, bool expected)
    {
        var inputs = new AcpPowerInputs(AcEss: true, DcEss: true, dc1, switching);

        Assert.Equal(expected, AcpPowerGate.IsPowered(AcpSide.Observer, inputs));
    }

    [Fact]
    public void Unknown_DefaultsToNormSwitching_SoDataLossNeverSwapsAcpsOut()
    {
        Assert.Equal(1, AcpPowerInputs.Unknown.AudioSwitching);
        // Still unpowered though — no bus data means no writes.
        Assert.False(AcpPowerGate.IsPowered(AcpSide.Captain, AcpPowerInputs.Unknown));
        Assert.False(AcpPowerGate.IsPowered(AcpSide.FirstOfficer, AcpPowerInputs.Unknown));
        Assert.False(AcpPowerGate.IsPowered(AcpSide.Observer, AcpPowerInputs.Unknown));
    }
}
