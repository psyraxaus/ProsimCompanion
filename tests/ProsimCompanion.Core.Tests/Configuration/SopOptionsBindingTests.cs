using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.DependencyInjection;
using Xunit;

namespace ProsimCompanion.Core.Tests.Configuration;

/// <summary>
/// The third instance of the binder list-append trap (after gsx and audio) — untested until #84
/// generalized the fix into AddOptionSection. These pin the SOP lists' behaviour through the
/// shared path.
/// </summary>
public sealed class SopOptionsBindingTests
{
    private static SopOptions Bind(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddCoreServices(configuration, "settings.json");
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<SopOptions>>().Value;
    }

    [Fact]
    public void FileContainingTheDefaultCallouts_DoesNotDoubleTheLists()
    {
        var defaults = SopOptions.DefaultAltitudeCallouts;
        var settings = new Dictionary<string, string?>();
        for (var i = 0; i < defaults.Count; i++)
        {
            settings[$"sop:altitudeCallouts:{i}:enabled"] = defaults[i].Enabled.ToString();
            settings[$"sop:altitudeCallouts:{i}:atFt"] = defaults[i].AtFt.ToString();
            settings[$"sop:altitudeCallouts:{i}:text"] = defaults[i].Text;
            settings[$"sop:altitudeCallouts:{i}:direction"] = defaults[i].Direction.ToString();
        }

        var options = Bind(settings);

        Assert.Equal(defaults.Count, options.AltitudeCallouts.Count);
        Assert.Equal(defaults.Select(c => c.AtFt), options.AltitudeCallouts.Select(c => c.AtFt));
    }

    [Fact]
    public void FileWithCustomGates_WinsExactly()
    {
        var options = Bind(new Dictionary<string, string?>
        {
            ["sop:approachGates:0:name"] = "800",
            ["sop:approachGates:0:aglFt"] = "800",
            ["sop:approachGates:0:maxSinkRateFpm"] = "900",
        });

        var gate = Assert.Single(options.ApproachGates);
        Assert.Equal("800", gate.Name);
        Assert.Equal(800, gate.AglFt);
        Assert.Equal(900, gate.MaxSinkRateFpm);
    }

    [Fact]
    public void FileOmittingTheLists_RestoresAllThreeDefaults()
    {
        var options = Bind([]);

        Assert.Equal(
            SopOptions.DefaultAltitudeCallouts.Select(c => c.AtFt),
            options.AltitudeCallouts.Select(c => c.AtFt));
        Assert.Equal(
            SopOptions.DefaultFlapPlacards.Select(p => (p.FlapHandle, p.MaxKt)),
            options.FlapPlacards.Select(p => (p.FlapHandle, p.MaxKt)));
        Assert.Equal(
            SopOptions.DefaultApproachGates.Select(g => g.Name),
            options.ApproachGates.Select(g => g.Name));
    }
}
