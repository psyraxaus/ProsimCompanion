using Microsoft.Extensions.DependencyInjection;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Hardware.Skalarki;
using ProsimCompanion.Core.State;
using Xunit;

namespace ProsimCompanion.Core.Tests.Web;

public sealed partial class SettingsPagesRenderTests
{
    [Theory]
    [InlineData("defaults")]
    [InlineData("configured")]
    public async Task SkalarkiSettings_Renders_WithoutLeakingTextIds(string fixture)
    {
        var configured = fixture == "configured";
        var services = new ServiceCollection();
        var options = new SkalarkiOptions();
        var store = new SkalarkiStatusStore();
        if (configured)
        {
            options.Enabled = true;
            options.SdkPath = @"C:\Program Files (x86)\SkalarkiIO Profiler 5.1\Tools";
            options.ProfilerHost = "10.0.1.21";
            options.EnabledDevices = ["GLARE", "RMP2"];
            options.DeviceLabels["RMP2"] = "RMP FO";
            store.Publish(SkalarkiSnapshot.Empty with
            {
                LinkState = ConnectionState.Connected,
                Devices = new Dictionary<SkalarkiDeviceId, SkalarkiDeviceView>
                {
                    [SkalarkiDeviceId.Glare] = new(true, true, null, null, null, null, 0),
                    [SkalarkiDeviceId.Rmp2] = new(true, false, DateTimeOffset.UtcNow, null, null, null, 0),
                    [SkalarkiDeviceId.Overhead] = new(false, true, null, null, null, null, 0),
                },
            });
        }

        Options(services, options);
        services.AddSingleton(store);
        services.AddSingleton<ISkalarkiSdkInspector>(new StubInspector(configured ? "5.1.919.1000" : null));

        var html = await RenderPageAsync<ProsimCompanion.Web.Pages.SkalarkiSettings>(services, "settings/hardware/skalarki", new Dictionary<string, object?>());

        Assert.Contains("<section", html, StringComparison.Ordinal);
        Assert.DoesNotMatch(LeakedTextId(), html);
        Assert.Contains("Test connection", html, StringComparison.Ordinal);
        if (configured)
        {
            Assert.Contains("Found SkalarkiIO.SDK.dll version 5.1.919.1000", html, StringComparison.Ordinal);
            Assert.Contains("RMP FO", html, StringComparison.Ordinal);          // the user's label
            Assert.Contains("OVERHEAD", html, StringComparison.Ordinal);        // reported but not ticked: offered
            Assert.Contains(">lost<", html, StringComparison.Ordinal);          // RMP2 flipped off
            Assert.Contains("2 boards ticked", html, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("Nothing ticked", html, StringComparison.Ordinal);
            Assert.Contains("DRAIMS2", html, StringComparison.Ordinal);         // nothing known yet: the whole list is offered
        }

        Dump($"skalarki-{fixture}", html);
    }

    private sealed class StubInspector(string? version) : ISkalarkiSdkInspector
    {
        public string? DescribeSdk(string? sdkPath) => string.IsNullOrWhiteSpace(sdkPath) ? null : version;

        public string? Validate(string? sdkPath) => version is null ? "No Skalarki SDK folder is set." : null;

        public Task<SkalarkiProbeResult> ProbeAsync(string host, int port, string? sdkPath, CancellationToken cancellationToken)
            => Task.FromResult(new SkalarkiProbeResult(false, "stub", [], [], null));
    }
}
