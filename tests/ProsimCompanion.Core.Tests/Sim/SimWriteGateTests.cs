using ProsimCompanion.Sim;
using Xunit;

namespace ProsimCompanion.Core.Tests.Sim;

public sealed class SimWriteGateTests
{
    [Theory]
    [InlineData("L:FSDT_GSX_MENU_CHOICE")]
    [InlineData("L:FSDT_GSX_SET_REMOTECONTROL")]
    [InlineData("l:fsdt_gsx_disable_doors_msg")]
    public void IsAllowed_GsxLvars_ReturnTrue(string name)
        => Assert.True(SimWriteGate.IsAllowed(name));

    /// <summary>Screen &amp; FOV (2026-10-28): the two camera SimVars of the post-apply nudge,
    /// exact names only.</summary>
    [Theory]
    [InlineData("COCKPIT CAMERA ZOOM")]
    [InlineData("camera action cockpit view reset")]
    public void IsAllowed_FovNudgeCameraVars_ReturnTrue(string name)
        => Assert.True(SimWriteGate.IsAllowed(name));

    [Theory]
    [InlineData("COCKPIT CAMERA ZOOM:1")]
    [InlineData("CAMERA ACTION COCKPIT VIEW SAVE:0")]
    [InlineData("COCKPIT CAMERA HEADLOOK")]
    public void IsAllowed_OtherCameraVars_ReturnFalse(string name)
        => Assert.False(SimWriteGate.IsAllowed(name));

    [Theory]
    [InlineData("PLANE ALTITUDE")]                 // never write flight dynamics
    [InlineData("L:S_OH_EXT_LT_BEACON")]           // cockpit switches are ProSim's domain
    [InlineData("L:FSDTGSX_TYPO")]                 // typo'd prefix must fail loudly
    [InlineData("")]
    public void IsAllowed_UnlistedNames_ReturnFalse(string name)
        => Assert.False(SimWriteGate.IsAllowed(name));

    [Fact]
    public void EnsureAllowed_UnlistedName_ThrowsWithGuidance()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => SimWriteGate.EnsureAllowed("L:SOME_RANDOM_VAR"));

        Assert.Contains("allow-list", ex.Message, StringComparison.Ordinal);
    }
}
