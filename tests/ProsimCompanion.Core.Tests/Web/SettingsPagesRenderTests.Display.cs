using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Display;
using ProsimCompanion.Core.Profiles;
using ProsimCompanion.Core.Tests.Speech;
using Xunit;

namespace ProsimCompanion.Core.Tests.Web;

public sealed partial class SettingsPagesRenderTests
{
    public static TheoryData<string, string> DisplaySections()
    {
        var data = new TheoryData<string, string>();
        foreach (var fixture in new[] { "defaults", "configured" })
        {
            foreach (var section in new[] { "displays", "apply", "multiwindow", "chaseplane" })
            {
                data.Add(section, fixture);
            }
        }

        return data;
    }

    /// <summary>Screen &amp; FOV (2026-10-28): every section over the neutral default and the
    /// design doc's reference rig (52" 16:9 at 1.5 m plus a side TV). The file scan runs
    /// against this machine, so the assertions never depend on a LocalCache being there.</summary>
    [Theory]
    [MemberData(nameof(DisplaySections))]
    public async Task DisplaySettings_RendersEverySection_WithoutLeakingTextIds(string section, string fixture)
    {
        var configured = fixture == "configured";
        var services = new ServiceCollection();
        Options(services, new DisplayOptions
        {
            Realism = 1.0,
            ApplyPitch = configured,
            Displays = configured
                ?
                [
                    new DisplayEntry { Name = "Main TV", Role = DisplayRole.Main, DiagonalInches = 52, DistanceMetres = 1.5 },
                    new DisplayEntry { Name = "Left TV", Role = DisplayRole.Left, DiagonalInches = 43, DistanceMetres = 1.2, YawDegrees = -50, CentreAboveEyeMetres = 0.1 },
                ]
                : [new DisplayEntry()],
        });
        services.AddSingleton(new AircraftProfileService(new FakeDataRefs(), new FixedOptionsMonitor<AircraftProfilesOptions>(new AircraftProfilesOptions()), new OfpStore(), NullLogger<AircraftProfileService>.Instance));
        services.AddSingleton(Mock.Of<IFovSimNudge>());

        var parameters = new Dictionary<string, object?> { ["Section"] = section };
        var html = await RenderPageAsync<ProsimCompanion.Web.Pages.DisplaySettings>(services, "settings/display/" + section, parameters);

        Assert.Contains("<section", html, StringComparison.Ordinal);
        Assert.DoesNotMatch(LeakedTextId(), html);
        switch (section)
        {
            case "displays":
                Assert.Contains(configured ? "Left TV" : "Add a screen", html, StringComparison.Ordinal);
                break;
            case "apply":
                Assert.Contains("InitialZoom", html, StringComparison.Ordinal);
                // The reference rig's zoom (design doc); the neutral default shows its own readout.
                Assert.Contains(configured ? "0.1367" : " rad", html, StringComparison.Ordinal);
                // The button exists only when this machine has a saved pilot view; otherwise
                // the page shows the Ctrl+F10 steps (or that there is no LocalCache at all).
                Assert.True(
                    html.Contains("Apply realistic FOV", StringComparison.Ordinal)
                    || html.Contains("No saved pilot view yet", StringComparison.Ordinal)
                    || html.Contains("No MSFS 2024 LocalCache folder", StringComparison.Ordinal),
                    "neither the Apply button nor the save-a-view instructions rendered");
                break;
            case "multiwindow":
                Assert.Contains(configured ? "-50.0°" : "No side or overhead screens", html, StringComparison.Ordinal);
                break;
            case "chaseplane":
                Assert.Contains("ChasePlane", html, StringComparison.Ordinal);
                break;
        }

        Dump($"display-{section}-{fixture}", html);
    }
}
