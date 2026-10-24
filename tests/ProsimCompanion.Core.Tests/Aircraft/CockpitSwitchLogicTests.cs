using ProsimCompanion.Core.Aircraft.Switches;
using ProsimCompanion.Core.Configuration;
using Xunit;

namespace ProsimCompanion.Core.Tests.Aircraft;

public sealed class CockpitSwitchLogicTests
{
    private static readonly IReadOnlyList<SwitchValue> TwoState = [new(0, "Normal"), new(1, "Pushed")];
    private static readonly IReadOnlyList<SwitchValue> Adirs = [new(0, "OFF"), new(1, "NAV"), new(2, "ATT")];

    [Fact]
    public void Toggle_FlipsBetweenTheFirstTwoValues_AndReturnsHomeFromElsewhere()
    {
        Assert.Equal(1, CockpitSwitchLogic.NextValue(CockpitPressMode.Toggle, 0, TwoState, longPress: false));
        Assert.Equal(0, CockpitSwitchLogic.NextValue(CockpitPressMode.Toggle, 1, TwoState, longPress: false));
        Assert.Equal(0, CockpitSwitchLogic.NextValue(CockpitPressMode.Toggle, 7, TwoState, longPress: false));
    }

    [Fact]
    public void Toggle_WithoutACatalogueList_Is0To1()
    {
        Assert.Equal(1, CockpitSwitchLogic.NextValue(CockpitPressMode.Toggle, 0, [], false));
        Assert.Equal(0, CockpitSwitchLogic.NextValue(CockpitPressMode.Toggle, 1, [], false));
    }

    [Fact]
    public void Cycle_StepsForwardAndWraps_LongPressStepsBack()
    {
        Assert.Equal(1, CockpitSwitchLogic.NextValue(CockpitPressMode.Cycle, 0, Adirs, false));
        Assert.Equal(2, CockpitSwitchLogic.NextValue(CockpitPressMode.Cycle, 1, Adirs, false));
        Assert.Equal(0, CockpitSwitchLogic.NextValue(CockpitPressMode.Cycle, 2, Adirs, false));
        Assert.Equal(2, CockpitSwitchLogic.NextValue(CockpitPressMode.Cycle, 0, Adirs, longPress: true));
        Assert.Equal(0, CockpitSwitchLogic.NextValue(CockpitPressMode.Cycle, 9, Adirs, false)); // unknown value → first
    }

    [Fact]
    public void Label_UsesTheCatalogue_ElseTheNumber()
    {
        Assert.Equal("Pushed", CockpitSwitchLogic.Label(TwoState, 1));
        Assert.Equal("5", CockpitSwitchLogic.Label(TwoState, 5));
    }

    [Fact]
    public void Evaluate_LowerLampFollowsTheSwitch_UnlessAnIndicatorIsNamed()
    {
        var row = new CockpitSwitchRow { Id = "lmv", DataRef = "system.switches.S_OH_HYD_LMV_BLUE" };

        var pushed = CockpitSwitchLogic.Evaluate(row, 1, null, null, stale: false, TwoState);
        Assert.True(pushed.LowerLit);
        Assert.False(pushed.UpperLit);
        Assert.Equal("Pushed", pushed.Position);

        row.LowerIndicator = "system.indicators.I_OH_HYD_LMV_BLUE_L";
        var dark = CockpitSwitchLogic.Evaluate(row, 1, null, 0, false, TwoState);
        Assert.False(dark.LowerLit); // the LED says dark even though the switch is pushed

        row.UpperIndicator = "system.indicators.I_X";
        var fault = CockpitSwitchLogic.Evaluate(row, 0, 1, 1, stale: true, TwoState);
        Assert.True(fault.UpperLit);
        Assert.True(fault.LowerLit);
        Assert.True(fault.Stale);

        var none = CockpitSwitchLogic.Evaluate(row, null, null, null, true, TwoState);
        Assert.Null(none.Value);
        Assert.Null(none.Position);
    }

    [Theory]
    [InlineData("system.switches.S_OH_HYD_LMV_BLUE", "oh_hyd_lmv_blue")]
    [InlineData("S_FCU_AP1", "fcu_ap1")]
    [InlineData("  system.switches.S_OH-ADIRS 1  ", "oh_adirs_1")]
    [InlineData("", "")]
    public void SlugFor_IsLowerAsciiWithUnderscores(string dataRef, string expected)
        => Assert.Equal(expected, CockpitSwitchLogic.SlugFor(dataRef));

    [Theory]
    [InlineData("#ffb000", true)]
    [InlineData("#FFF", true)]
    [InlineData(" #3ea6ff ", true)]
    [InlineData("ffb000", false)]
    [InlineData("#ffb00", false)]
    [InlineData("#gggggg", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsHexColor_AcceptsOnlyShortOrLongHex(string? text, bool expected)
        => Assert.Equal(expected, CockpitSwitchLogic.IsHexColor(text));

    [Fact]
    public void FindRow_IsCaseInsensitiveAndTrimmed()
    {
        var rows = new List<CockpitSwitchRow> { new() { Id = "Oh_Hyd_Lmv_Blue" } };
        Assert.NotNull(CockpitSwitchLogic.FindRow(rows, " oh_hyd_lmv_blue "));
        Assert.Null(CockpitSwitchLogic.FindRow(rows, "other"));
        Assert.Null(CockpitSwitchLogic.FindRow(rows, " "));
    }
}
