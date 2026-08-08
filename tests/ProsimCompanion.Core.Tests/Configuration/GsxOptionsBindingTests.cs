using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.DependencyInjection;
using Xunit;

namespace ProsimCompanion.Core.Tests.Configuration;

/// <summary>
/// Round-4 regression: the config binder APPENDS array items to a list the options class
/// already initialized, so a settings file containing the default departure list produced a
/// doubled list — and every departure service was triggered twice per pump.
/// </summary>
public sealed class GsxOptionsBindingTests
{
    private static GsxOptions Bind(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddCoreServices(configuration, "settings.json");
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<GsxOptions>>().Value;
    }

    [Fact]
    public void FileContainingTheDefaultSteps_DoesNotDoubleTheList()
    {
        var defaults = GsxOptions.DefaultDepartureServices;
        var settings = new Dictionary<string, string?>();
        for (var i = 0; i < defaults.Count; i++)
        {
            settings[$"gsx:departureServices:{i}:service"] = defaults[i].Service;
            settings[$"gsx:departureServices:{i}:activation"] = defaults[i].Activation.ToString();
            settings[$"gsx:departureServices:{i}:constraint"] = defaults[i].Constraint.ToString();
        }

        var options = Bind(settings);

        Assert.Equal(defaults.Count, options.DepartureServices.Count);
        Assert.Equal(defaults.Select(s => s.Service), options.DepartureServices.Select(s => s.Service));
    }

    [Fact]
    public void FileWithCustomSteps_WinsExactly_AndParsesCamelCaseEnums()
    {
        var options = Bind(new Dictionary<string, string?>
        {
            ["gsx:departureServices:0:service"] = "Catering",
            ["gsx:departureServices:0:activation"] = "afterPrevCompleted",
            ["gsx:departureServices:0:constraint"] = "turnAround",
            ["gsx:departureServices:0:minimumFlightMinutes"] = "45",
            ["gsx:departureServices:1:service"] = "Boarding",
            ["gsx:departureServices:1:activation"] = "manual",
        });

        Assert.Equal(2, options.DepartureServices.Count);
        Assert.Equal("Catering", options.DepartureServices[0].Service);
        Assert.Equal(GsxServiceActivation.AfterPrevCompleted, options.DepartureServices[0].Activation);
        Assert.Equal(GsxServiceConstraint.TurnAround, options.DepartureServices[0].Constraint);
        Assert.Equal(45, options.DepartureServices[0].MinimumFlightMinutes);
        Assert.Equal(GsxServiceActivation.Manual, options.DepartureServices[1].Activation);
        Assert.Equal(GsxServiceConstraint.Always, options.DepartureServices[1].Constraint);
        Assert.Equal(0, options.DepartureServices[1].MinimumFlightMinutes);
    }

    [Fact]
    public void FileOmittingTheSteps_RestoresDefaults()
    {
        var options = Bind([]);

        Assert.Equal(
            GsxOptions.DefaultDepartureServices.Select(s => (s.Service, s.Activation, s.Constraint)),
            options.DepartureServices.Select(s => (s.Service, s.Activation, s.Constraint)));
    }
}
