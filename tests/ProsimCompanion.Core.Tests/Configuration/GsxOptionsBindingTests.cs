using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.DependencyInjection;
using ProsimCompanion.Core.Profiles;
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

    // EHAM 2026-11-01: the aircraft profile's stored gsxSettings block is a whole GsxOptions the
    // binder creates itself — its six default steps were already in the list when the profile's
    // five were appended, and the applier wrote eleven into the file.
    [Fact]
    public void ProfileGsxBlock_DoesNotGetTheDefaultsPrependedToItsList()
    {
        var settings = new Dictionary<string, string?>
        {
            ["aircraftProfiles:profiles:0:id"] = "p1",
            ["aircraftProfiles:profiles:0:name"] = "British Airways",
        };
        string[] profileSteps = ["Cleaning", "Lavatory", "Refueling", "Catering", "Water"];
        for (var i = 0; i < profileSteps.Length; i++)
        {
            settings[$"aircraftProfiles:profiles:0:gsxSettings:departureServices:{i}:service"] = profileSteps[i];
            settings[$"aircraftProfiles:profiles:0:gsxSettings:departureServices:{i}:activation"] = "afterCalled";
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddCoreServices(configuration, "settings.json");
        using var provider = services.BuildServiceProvider();
        var profiles = provider.GetRequiredService<IOptions<AircraftProfilesOptions>>().Value;

        var block = Assert.Single(profiles.Profiles).GsxSettings!;
        Assert.Equal(profileSteps, block.DepartureServices.Select(s => s.Service));
    }

    [Fact]
    public void DuplicatedStepsInTheFile_AreDroppedKeepingTheFirst()
    {
        var options = Bind(new Dictionary<string, string?>
        {
            ["gsx:departureServices:0:service"] = "Catering",
            ["gsx:departureServices:0:activation"] = "afterCalled",
            ["gsx:departureServices:1:service"] = "Boarding",
            ["gsx:departureServices:1:activation"] = "afterAllCompleted",
            ["gsx:departureServices:2:service"] = "Catering",
            ["gsx:departureServices:2:activation"] = "manual",
        });

        Assert.Equal(["Catering", "Boarding"], options.DepartureServices.Select(s => s.Service));
        Assert.Equal(GsxServiceActivation.AfterCalled, options.DepartureServices[0].Activation);
    }

    [Fact]
    public void Normalize_DropsDuplicatesAndBlanks_KeepsARemovedServiceRemoved()
    {
        var steps = new List<DepartureServiceStep>
        {
            new("Refueling", GsxServiceActivation.AfterCalled),
            new("Catering", GsxServiceActivation.AfterCalled),
            new("Refueling", GsxServiceActivation.Manual),
            new("", GsxServiceActivation.Manual),
        };

        Assert.Equal(2, GsxOptions.NormalizeDepartureServices(steps));
        Assert.Equal(["Refueling", "Catering"], steps.Select(s => s.Service)); // no Boarding appended: removed stays removed
        Assert.Equal(0, GsxOptions.NormalizeDepartureServices(steps));
    }
}
