using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.DependencyInjection;
using Xunit;

namespace ProsimCompanion.Core.Tests.Configuration;

/// <summary>
/// Round-4 regression: the config binder APPENDS array items to a list the options class
/// already initialized, so a settings file containing the default departure order produced a
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
    public void FileContainingTheDefaultOrder_DoesNotDoubleTheList()
    {
        var settings = new Dictionary<string, string?>();
        for (var i = 0; i < GsxOptions.DefaultDepartureServiceOrder.Count; i++)
        {
            settings[$"gsx:departureServiceOrder:{i}"] = GsxOptions.DefaultDepartureServiceOrder[i];
        }

        var options = Bind(settings);

        Assert.Equal(GsxOptions.DefaultDepartureServiceOrder, options.DepartureServiceOrder);
    }

    [Fact]
    public void FileWithCustomOrder_WinsExactly()
    {
        var options = Bind(new Dictionary<string, string?>
        {
            ["gsx:departureServiceOrder:0"] = "Catering",
            ["gsx:departureServiceOrder:1"] = "Boarding",
        });

        Assert.Equal(["Catering", "Boarding"], options.DepartureServiceOrder);
    }

    [Fact]
    public void FileOmittingTheOrder_RestoresDefaults()
    {
        var options = Bind([]);

        Assert.Equal(GsxOptions.DefaultDepartureServiceOrder, options.DepartureServiceOrder);
    }
}
