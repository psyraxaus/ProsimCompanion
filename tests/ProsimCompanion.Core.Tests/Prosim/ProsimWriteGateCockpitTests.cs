using ProsimCompanion.Prosim.DataRefs;
using Xunit;

namespace ProsimCompanion.Core.Tests.Prosim;

/// <summary>The cockpit-switch hook on the write gate (ADR-0015): it can only ever open
/// <c>system.switches.S_*</c> names, and only while a predicate is installed and says yes.</summary>
[Collection("ProsimWriteGateHook")]
public sealed class ProsimWriteGateCockpitTests : IDisposable
{
    public ProsimWriteGateCockpitTests() => ProsimWriteGate.ConfiguredSwitchAllowed = null;

    public void Dispose() => ProsimWriteGate.ConfiguredSwitchAllowed = null;

    [Fact]
    public void WithoutAHook_ACockpitSwitchIsRefused()
        => Assert.False(ProsimWriteGate.IsAllowed("system.switches.S_OH_HYD_LMV_BLUE"));

    [Fact]
    public void WithAHook_OnlyTheNamesItAdmits_InTheSwitchFamily_Pass()
    {
        ProsimWriteGate.ConfiguredSwitchAllowed = name => name.EndsWith("_BLUE", StringComparison.Ordinal);

        Assert.True(ProsimWriteGate.IsAllowed("system.switches.S_OH_HYD_LMV_BLUE"));
        Assert.False(ProsimWriteGate.IsAllowed("system.switches.S_OH_HYD_LMV_GREEN"));
        // A circuit breaker or anything outside S_ never passes, whatever the hook says.
        ProsimWriteGate.ConfiguredSwitchAllowed = _ => true;
        Assert.False(ProsimWriteGate.IsAllowed("system.switches.CB_N35_GYB_LEAK_TEST"));
        Assert.False(ProsimWriteGate.IsAllowed("system.indicators.I_OH_HYD_LMV_BLUE_L"));
        Assert.False(ProsimWriteGate.IsAllowed("aircraft.engines.1.n1"));
    }

    [Fact]
    public void TheStaticLists_StillWork_WithAHookInstalled()
    {
        ProsimWriteGate.ConfiguredSwitchAllowed = _ => false;
        Assert.True(ProsimWriteGate.IsAllowed("system.switches.S_FCU_AP1")); // the FCU prefix rule
        Assert.False(ProsimWriteGate.IsAllowed("system.switches.S_OH_HYD_LMV_BLUE"));
    }
}
