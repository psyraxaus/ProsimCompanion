using ProsimCompanion.App.Hosting;
using ProsimCompanion.Core.Aircraft.Switches;
using ProsimCompanion.Core.Configuration;
using Xunit;

namespace ProsimCompanion.Core.Tests.App;

public sealed class ControlsApiTests
{
    private static readonly SwitchCatalogSnapshot Shipped = SwitchCatalog.LoadShipped();

    [Fact]
    public void Build_SkipsBlankRows_SanitisesColours_AndCarriesCataloguePositions()
    {
        var cockpit = new CockpitSwitchesOptions
        {
            Rows =
            [
                new CockpitSwitchRow { Id = "oh_hyd_lmv_blue", Name = "", DataRef = "system.switches.S_OH_HYD_LMV_BLUE", UpperColor = "not-a-colour", BezelColor = "#5fb4ff", ShowUnlitLegend = false },
                new CockpitSwitchRow { Id = "", DataRef = "system.switches.S_OH_HYD_LMV_GREEN" },
                new CockpitSwitchRow { Id = "nothing", DataRef = "" },
            ],
        };
        var relay = new InputRelayOptions { Enabled = true, Views = [new InputViewButton { Name = "Wing", Button = 5 }, new InputViewButton { Name = " " }] };

        var response = ControlsApiEndpoints.Build(cockpit, relay, Shipped);

        var row = Assert.Single(response.Switches);
        Assert.Equal("oh_hyd_lmv_blue", row.Id);
        Assert.Equal("oh_hyd_lmv_blue", row.Name); // blank name falls back to the id
        Assert.Equal("#ffb000", row.UpperColor);   // bad hex → default amber
        Assert.Equal("#5fb4ff", row.BezelColor);
        Assert.Equal(CockpitSwitchLook.Korry2, row.Look);
        Assert.False(row.ShowUnlitLegend);
        Assert.Equal([0, 1], row.Positions.Select(p => p.Value));
        Assert.Equal("Pushed", row.Positions[1].Label);

        var view = Assert.Single(response.Views);
        Assert.Equal("Wing", view.Name);
        Assert.True(response.SwitchesEnabled);
        Assert.True(response.ViewsEnabled);
    }

    [Fact]
    public void Build_WithoutACatalogue_HasNoPositions()
    {
        var cockpit = new CockpitSwitchesOptions { Rows = [new CockpitSwitchRow { Id = "a", DataRef = "system.switches.S_X" }] };

        var response = ControlsApiEndpoints.Build(cockpit, new InputRelayOptions(), null);

        Assert.Empty(Assert.Single(response.Switches).Positions);
    }

    [Fact]
    public void ShippedCatalogue_KnowsTheThreeLeakValves_WithNormalAndPushed()
    {
        foreach (var colour in new[] { "BLUE", "GREEN", "YELLOW" })
        {
            var entry = Shipped.Find("system.switches.S_OH_HYD_LMV_" + colour);
            Assert.NotNull(entry);
            Assert.Equal(["Normal", "Pushed"], entry.Values.Select(v => v.Label));
        }

        Assert.Contains("system.indicators.I_OH_HYD_LMV_BLUE_L", SwitchCatalog.ShippedIndicatorNames);
    }
}
