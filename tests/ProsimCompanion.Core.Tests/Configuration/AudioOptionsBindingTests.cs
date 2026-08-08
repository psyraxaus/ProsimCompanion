using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.DependencyInjection;
using Xunit;

namespace ProsimCompanion.Core.Tests.Configuration;

/// <summary>
/// Same binder-appends-to-defaults trap as the GSX departure list: a settings file containing
/// the default app mappings must not produce a doubled list, and omitted lists must fall back
/// to the defaults.
/// </summary>
public sealed class AudioOptionsBindingTests
{
    private static AudioOptions Bind(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddCoreServices(configuration, "settings.json");
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<AudioOptions>>().Value;
    }

    [Fact]
    public void FileContainingTheDefaultMappings_DoesNotDoubleTheList()
    {
        var defaults = AudioOptions.DefaultAppMappings;
        var settings = new Dictionary<string, string?>();
        for (var i = 0; i < defaults.Count; i++)
        {
            settings[$"audio:appMappings:{i}:channel"] = defaults[i].Channel.ToString();
            settings[$"audio:appMappings:{i}:binary"] = defaults[i].Binary;
        }

        var options = Bind(settings);

        Assert.Equal(defaults.Count, options.AppMappings.Count);
        Assert.Equal(defaults.Select(m => m.Binary), options.AppMappings.Select(m => m.Binary));
    }

    [Fact]
    public void FileOmittingTheLists_RestoresDefaults()
    {
        var options = Bind([]);

        Assert.Equal(
            AudioOptions.DefaultAppMappings.Select(m => (m.Channel, m.Binary)),
            options.AppMappings.Select(m => (m.Channel, m.Binary)));
        Assert.Equal([AcpSide.Captain], options.ActiveAcps);
    }

    [Fact]
    public void CamelCaseEnumsAndVoiceMeeterDictionary_BindCorrectly()
    {
        var options = Bind(new Dictionary<string, string?>
        {
            ["audio:backend"] = "voiceMeeter",
            ["audio:coreAudioAcp"] = "firstOfficer",
            ["audio:activeAcps:0"] = "captain",
            ["audio:activeAcps:1"] = "observer",
            ["audio:appMappings:0:channel"] = "intercom",
            ["audio:appMappings:0:binary"] = "Couatl64_MSFS",
            ["audio:appMappings:0:useLatch"] = "false",
            ["audio:voiceMeeterMappings:captain:0:channel"] = "vhf1",
            ["audio:voiceMeeterMappings:captain:0:stripIndex"] = "2",
            ["audio:voiceMeeterMappings:captain:0:isBus"] = "true",
        });

        Assert.Equal(AudioBackend.VoiceMeeter, options.Backend);
        Assert.Equal(AcpSide.FirstOfficer, options.CoreAudioAcp);
        Assert.Equal([AcpSide.Captain, AcpSide.Observer], options.ActiveAcps);
        var mapping = Assert.Single(options.AppMappings);
        Assert.Equal(AudioChannel.Intercom, mapping.Channel);
        Assert.False(mapping.UseLatch);
        var vm = Assert.Single(options.VoiceMeeterMappings["captain"]);
        Assert.Equal(AudioChannel.Vhf1, vm.Channel);
        Assert.Equal(2, vm.StripIndex);
        Assert.True(vm.IsBus);
    }

    [Fact]
    public void DeviceFilter_DefaultsToActiveRender_AndBindsOverrides()
    {
        var defaults = Bind([]);
        Assert.Equal("render", defaults.DeviceFilterFlow);
        Assert.Equal("active", defaults.DeviceFilterState);

        var options = Bind(new Dictionary<string, string?>
        {
            ["audio:deviceFilterFlow"] = "all",
            ["audio:deviceFilterState"] = "unplugged",
        });
        Assert.Equal("all", options.DeviceFilterFlow);
        Assert.Equal("unplugged", options.DeviceFilterState);
    }

    [Fact]
    public void DefaultMappings_CoverTheDocumentedChannels()
    {
        var defaults = AudioOptions.DefaultAppMappings;

        // docs/integrations/audio.md: VHF1 → ATC clients, INT → Couatl (GSX), CAB → the sim.
        Assert.Contains(defaults, m => m.Channel == AudioChannel.Vhf1 && m.Binary == "vPilot");
        Assert.Contains(defaults, m => m.Channel == AudioChannel.Intercom && m.Binary == "Couatl64_MSFS");
        Assert.Contains(defaults, m => m.Channel == AudioChannel.Cabin && m.Binary == "FlightSimulator");
        Assert.All(defaults, m => Assert.True(m.UseLatch));
        Assert.All(defaults, m => Assert.True(m.OnlyActive));
    }
}
