using ProsimCompanion.Core.Aircraft.Switches;
using Xunit;

namespace ProsimCompanion.Core.Tests.Aircraft;

/// <summary>The hybrid switch list behind the smart-button picker (2026-10-17): the shipped
/// CSV parses into grouped, value-annotated switches, and a live ProSim list builds the same
/// shape.</summary>
public sealed class SwitchCatalogTests
{
    [Fact]
    public void ShippedCsv_HoldsTheCockpitSwitchesWithTheirValues()
    {
        var shipped = SwitchCatalog.LoadShipped();

        Assert.Equal(SwitchCatalogSource.Shipped, shipped.Source);
        Assert.Null(shipped.ReadAtUtc);
        Assert.True(shipped.Switches.Count > 1000, $"only {shipped.Switches.Count} switches");
        Assert.All(shipped.Switches, s => Assert.StartsWith(SwitchCatalog.Prefix, s.Name, StringComparison.Ordinal));

        var intRad = shipped.Find("system.switches.S_ASP_INTRAD");
        Assert.NotNull(intRad);
        Assert.Equal("ASP INT/RAD Switch", intRad.Title);
        Assert.Equal("audio", intRad.Group);
        Assert.Equal("S_ASP_INTRAD", intRad.ShortName);
        Assert.Equal([new SwitchValue(0, "INT"), new SwitchValue(1, "Off"), new SwitchValue(2, "RAD")], intRad.Values);
    }

    [Fact]
    public void Find_IsCaseInsensitiveAndTrimmed_AndNullForUnknown()
    {
        var shipped = SwitchCatalog.LoadShipped();

        Assert.NotNull(shipped.Find("  system.switches.s_asp_cab_send "));
        Assert.Null(shipped.Find("system.switches.S_DOES_NOT_EXIST"));
        Assert.Null(shipped.Find(""));
        Assert.Null(shipped.Find(null));
    }

    [Theory]
    [InlineData("system.switches.S_OH_ELEC_BAT1", "overhead")]
    [InlineData("system.switches.S_PED_SPD_BRAKE", "pedestal")]
    [InlineData("system.switches.S_CDU2_KEY_PROG", "mcdu")]
    [InlineData("system.switches.S_ASP2_INTRAD", "audio")]
    [InlineData("system.switches.S_HAND_MIC_PTT_CAPT", "audio")]
    [InlineData("system.switches.S_FCU_EFIS2_NDB", "fcu")]
    [InlineData("system.switches.S_MIP_ISIS_PLUS", "mainPanel")]
    [InlineData("system.switches.S_ECAM_ALL", "ecam")]
    [InlineData("system.switches.S_DCDU1_PGE_PLUS", "dcdu")]
    [InlineData("system.switches.S_XPDR_ALTREPORTING", "radios")]
    [InlineData("system.switches.S_WR2_GAIN", "radios")]
    [InlineData("system.switches.S_FC_CAPT_INST_DISCONNECT", "flightControls")]
    [InlineData("system.switches.S_ENG_MASTER_1", "engines")]
    [InlineData("system.switches.CB_A01_HP_FUEL_SOV_1", "circuitBreakers")]
    [InlineData("system.switches.S_USER_1", "other")]
    [InlineData("system.switches.S_NEWPANEL_THING", "other")]
    public void GroupOf_MapsEveryPanelPrefixToAKnownGroup(string name, string expected)
    {
        Assert.Equal(expected, SwitchCatalog.GroupOf(name));
        Assert.Contains(expected, SwitchCatalog.GroupOrder);
    }

    [Fact]
    public void ParseDescription_SplitsTitleAndValueList()
    {
        var (title, values) = SwitchCatalog.ParseDescription("Pack flow [0:Lo, 1:Norm, 2:Hi]");

        Assert.Equal("Pack flow", title);
        Assert.Equal(3, values.Count);
        Assert.Equal(new SwitchValue(2, "Hi"), values[2]);
    }

    [Fact]
    public void ParseDescription_WithoutAValueList_KeepsTheTextAndNoValues()
    {
        Assert.Equal(("Refuel preselect", (IReadOnlyList<SwitchValue>)[]), SwitchCatalog.ParseDescription("Refuel preselect"));
        // A bracket that is not "number:label" is a note, not a list.
        var (title, values) = SwitchCatalog.ParseDescription("Gain [range -5 to 5]");
        Assert.Equal("Gain [range -5 to 5]", title);
        Assert.Empty(values);
        Assert.Equal(("", (IReadOnlyList<SwitchValue>)[]), SwitchCatalog.ParseDescription(null));
    }

    [Fact]
    public void Build_FromLiveRows_FiltersToReadableSwitchesAndStampsTheSource()
    {
        var readAt = new DateTimeOffset(2026, 10, 17, 9, 0, 0, TimeSpan.Zero);
        var rows = new[]
        {
            new SwitchCatalogRow("system.switches.S_OH_NEW_SWITCH", "Brand new [0:Off, 1:On]", true),
            new SwitchCatalogRow("system.switches.S_OH_WRITE_ONLY", "Write only [0:Off, 1:On]", false),
            new SwitchCatalogRow("aircraft.altitude", "Not a switch", true),
            new SwitchCatalogRow("system.switches.S_USER_PLAIN", "", true),
        };

        var live = SwitchCatalog.Build(rows, SwitchCatalogSource.Live, readAt);

        Assert.Equal(SwitchCatalogSource.Live, live.Source);
        Assert.Equal(readAt, live.ReadAtUtc);
        Assert.Equal(["system.switches.S_OH_NEW_SWITCH", "system.switches.S_USER_PLAIN"], live.Switches.Select(s => s.Name));
        Assert.Equal("Brand new", live.Switches[0].Title);
        // An empty description falls back to the short name so the row is never blank.
        Assert.Equal("S_USER_PLAIN", live.Switches[1].Title);
        Assert.Empty(live.Switches[1].Values);
    }

    [Fact]
    public void ParseCsv_HonoursQuotedFieldsWithCommasAndDoubledQuotes()
    {
        using var reader = new StringReader(
            "Name,Description,Data Type,Data Unit,Can Read,Can Write\n" +
            "system.switches.S_A,\"Alpha [0:Off, 1:On]\",System.Int32,,TRUE,TRUE\n" +
            "companyRoutes.add,\"Add (format: \"\"name:route\"\")\",System.String,,FALSE,TRUE\n" +
            "broken line\n");

        var rows = SwitchCatalog.ParseCsv(reader);

        Assert.Equal(2, rows.Count);
        Assert.Equal("Alpha [0:Off, 1:On]", rows[0].Description);
        Assert.True(rows[0].CanRead);
        Assert.Equal("Add (format: \"name:route\")", rows[1].Description);
        Assert.False(rows[1].CanRead);
    }
}
