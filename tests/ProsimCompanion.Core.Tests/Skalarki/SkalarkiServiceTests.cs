using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Hardware.Skalarki;
using ProsimCompanion.Core.State;
using ProsimCompanion.Skalarki;
using Xunit;

namespace ProsimCompanion.Core.Tests.Skalarki;

/// <summary>Options defaults, the SDK loader's refusals (no SDK needed), and the connection
/// service's no-op when the feature is off or the folder does not validate.</summary>
public sealed class SkalarkiServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pc-skalarki-" + Guid.NewGuid().ToString("N"));

    public SkalarkiServiceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Options_Defaults_AreTheProfilersOwn()
    {
        var o = new SkalarkiOptions();
        Assert.False(o.Enabled);
        Assert.Equal("", o.SdkPath);
        Assert.Equal("127.0.0.1", o.ProfilerHost);
        Assert.Equal(53000, o.ProfilerPort);
        Assert.Equal(SkalarkiCatalogue.A32X, o.Catalogue);
        Assert.Empty(o.EnabledDevices);
        Assert.Empty(o.DeviceLabels);
        Assert.Equal(2000, o.LampTestMs);
        Assert.Equal(5000, o.ReconnectBackoffMs);
        Assert.Equal("skalarki", SkalarkiOptions.SectionName);
    }

    [Fact]
    public void Options_BackoffFloor_EnabledIds_AndLabels()
    {
        var o = new SkalarkiOptions
        {
            ReconnectBackoffMs = 10,
            EnabledDevices = ["MCDU1", "rmp2", "bogus", "mcdu1"],
            DeviceLabels = { ["RMP2"] = "RMP FO" },
        };
        Assert.Equal(TimeSpan.FromSeconds(1), o.ReconnectBackoff);
        Assert.Equal([SkalarkiDeviceId.Mcdu1, SkalarkiDeviceId.Rmp2], o.EnabledDeviceIds());
        Assert.Equal("RMP FO", o.LabelFor(SkalarkiDeviceId.Rmp2));
        Assert.Equal("MCDU 1", o.LabelFor(SkalarkiDeviceId.Mcdu1));
    }

    [Fact]
    public void SdkLoader_RefusesEmpty_MissingDll_MissingSupportFile_AndMissingBridge()
    {
        var loader = new SdkLoader(NullLoggerFactory.Instance, _dir);
        Assert.Equal("No Skalarki SDK folder is set.", loader.Validate(""));
        Assert.Contains("was not found", loader.Validate(_dir), StringComparison.Ordinal);

        File.WriteAllBytes(Path.Combine(_dir, SdkLoader.SdkFileName), [0]);
        Assert.Contains("SkalarkiIO.SDK.Telemetry.dll is missing", loader.Validate(_dir), StringComparison.Ordinal);

        foreach (var file in SdkLoader.RequiredFiles)
        {
            File.WriteAllBytes(Path.Combine(_dir, file), [0]);
        }

        Assert.Contains("no Skalarki SDK bridge", loader.Validate(_dir), StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => loader.Create(_dir));

        // A full dll path is accepted as the folder.
        Assert.Equal(_dir, SdkLoader.Locate(Path.Combine(_dir, SdkLoader.SdkFileName)));
        Assert.Null(SdkLoader.Locate(Path.Combine(_dir, "nothing.dll")));
    }

    [Fact]
    public async Task Service_Disabled_IsANoOp_TouchesNoFactory_PublishesDisabledWithoutALogLine()
    {
        var factory = new CountingFactory();
        var store = new SkalarkiStatusStore();
        var status = new ConnectionStatusStore();
        var monitor = new FixedOptionsMonitor<SkalarkiOptions>(new SkalarkiOptions { Enabled = false });
        var service = new SkalarkiConnectionService(monitor, factory, store, status, NullLogger<SkalarkiConnectionService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(100);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(0, factory.ValidateCalls);
        Assert.Equal(0, factory.CreateCalls);
        Assert.Equal(ConnectionState.Disabled, store.Snapshot().LinkState);
        Assert.Equal(ConnectionState.Disabled, status.Snapshot().Single(p => p.Key == Subsystems.Skalarki).Value);
    }

    [Fact]
    public async Task Service_Enabled_WithABadFolder_StaysDisabledWithTheReason_AndNeverCreatesAClient()
    {
        var factory = new CountingFactory { Refusal = "SkalarkiIO.SDK.dll was not found in the configured Skalarki SDK folder." };
        var store = new SkalarkiStatusStore();
        var status = new ConnectionStatusStore();
        var monitor = new FixedOptionsMonitor<SkalarkiOptions>(new SkalarkiOptions { Enabled = true, SdkPath = @"C:\nowhere" });
        var service = new SkalarkiConnectionService(monitor, factory, store, status, NullLogger<SkalarkiConnectionService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(100);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(1, factory.ValidateCalls);
        Assert.Equal(0, factory.CreateCalls);
        Assert.Equal(ConnectionState.Disabled, store.Snapshot().LinkState);
        Assert.Equal(factory.Refusal, store.Snapshot().Reason);
        Assert.Equal(factory.Refusal, status.ReasonOf(Subsystems.Skalarki));
    }

    [Fact]
    public async Task Service_Enabled_ConnectFails_ReportsDisconnected_AndStopsCleanly()
    {
        var client = new FakeClient { ConnectFailure = new IOException("refused") };
        var factory = new CountingFactory { Client = client };
        var store = new SkalarkiStatusStore();
        var status = new ConnectionStatusStore();
        var monitor = new FixedOptionsMonitor<SkalarkiOptions>(new SkalarkiOptions { Enabled = true, SdkPath = _dir, ProfilerHost = "10.0.0.9", ReconnectBackoffMs = 60_000 });
        var service = new SkalarkiConnectionService(monitor, factory, store, status, NullLogger<SkalarkiConnectionService>.Instance);

        await service.StartAsync(CancellationToken.None);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (store.Snapshot().LinkState != ConnectionState.Disconnected && sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(20);
        }

        Assert.Equal(ConnectionState.Disconnected, store.Snapshot().LinkState);
        Assert.Contains("10.0.0.9:53000", store.Snapshot().Reason, StringComparison.Ordinal);
        Assert.Equal("5.1.0", store.Snapshot().SdkVersion);

        sw.Restart();
        await service.StopAsync(CancellationToken.None);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), "shutdown must not wait out the backoff");
        Assert.True(client.Disposed);
    }

    [Fact]
    public async Task Service_Connected_FeedsTheStoreFromClientEvents_AndRegistersTheCatalogue()
    {
        var client = new FakeClient();
        var factory = new CountingFactory { Client = client };
        var store = new SkalarkiStatusStore();
        var status = new ConnectionStatusStore();
        var monitor = new FixedOptionsMonitor<SkalarkiOptions>(new SkalarkiOptions { Enabled = true, SdkPath = _dir, EnabledDevices = ["GLARE", "RMP2"] });
        var service = new SkalarkiConnectionService(monitor, factory, store, status, NullLogger<SkalarkiConnectionService>.Instance);

        await service.StartAsync(CancellationToken.None);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (store.Snapshot().LinkState != ConnectionState.Connected && sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(20);
        }

        Assert.Equal(ConnectionState.Connected, store.Snapshot().LinkState);
        Assert.Equal("200200", store.Snapshot().License);
        Assert.Equal([SkalarkiDeviceId.Glare, SkalarkiDeviceId.Rmp2], client.Registered);
        Assert.Equal(SkalarkiCatalogue.A32X, client.RegisteredCatalogue);

        client.RaiseDevice(SkalarkiDeviceId.Glare, true);
        client.RaiseDevice(SkalarkiDeviceId.Rmp2, false);
        client.RaiseEvent(new SkalarkiHardwareEvent(SkalarkiDeviceId.Glare, "FCU", SkalarkiSourceKind.Switch, "FCU_HDG_PUSH", "ON", DateTimeOffset.UtcNow));
        await Task.Delay(50);

        var snap = store.Snapshot();
        Assert.True(snap.Devices[SkalarkiDeviceId.Glare].Connected);
        Assert.Equal(1, snap.Devices[SkalarkiDeviceId.Glare].EventCount);
        Assert.Equal("FCU_HDG_PUSH", snap.Devices[SkalarkiDeviceId.Glare].LastControl);
        Assert.False(snap.Devices[SkalarkiDeviceId.Rmp2].Connected);

        // The Profiler drops the link: the store says so and the service goes back to retrying.
        client.RaiseLink(false);
        sw.Restart();
        while (store.Snapshot().LinkState != ConnectionState.Disconnected && sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(20);
        }

        Assert.Equal(ConnectionState.Disconnected, store.Snapshot().LinkState);
        Assert.False(store.Snapshot().Devices[SkalarkiDeviceId.Glare].Connected);

        await service.StopAsync(CancellationToken.None);
        Assert.True(client.Disposed);
    }

    private sealed class CountingFactory : ISkalarkiClientFactory
    {
        public int ValidateCalls { get; private set; }

        public int CreateCalls { get; private set; }

        public string? Refusal { get; init; }

        public FakeClient? Client { get; init; }

        public string? Validate(string sdkPath)
        {
            ValidateCalls++;
            return Refusal;
        }

        public ISkalarkiClient Create(string sdkPath)
        {
            CreateCalls++;
            return Client ?? throw new InvalidOperationException("no client in this test");
        }
    }

    internal sealed class FakeClient : ISkalarkiClient
    {
        public event Action<bool>? LinkChanged;

        public event Action<SkalarkiDeviceId, bool>? DeviceChanged;

        public event Action<SkalarkiHardwareEvent>? HardwareEvent;

        public string SdkVersion => "5.1.0";

        public Exception? ConnectFailure { get; init; }

        public bool Disposed { get; private set; }

        public List<SkalarkiDeviceId> Registered { get; } = [];

        public SkalarkiCatalogue RegisteredCatalogue { get; private set; }

        public List<(SkalarkiDeviceId Device, bool On)> Outputs { get; } = [];

        public Task ConnectAsync(string host, int port, CancellationToken cancellationToken)
        {
            if (ConnectFailure is not null)
            {
                throw ConnectFailure;
            }

            LinkChanged?.Invoke(true);
            return Task.CompletedTask;
        }

        public Task DisconnectAsync() => Task.CompletedTask;

        public void RegisterAll(IReadOnlyCollection<SkalarkiDeviceId> devices, SkalarkiCatalogue catalogue)
        {
            Registered.Clear();
            Registered.AddRange(devices);
            RegisteredCatalogue = catalogue;
        }

        public void SetDeviceOutputs(SkalarkiDeviceId device, bool on) => Outputs.Add((device, on));

        public void HardwareReset(SkalarkiDeviceId device, bool hard)
        {
        }

        public Task<string?> GetLicenseAsync(CancellationToken cancellationToken) => Task.FromResult<string?>("200200");

        public void Dispose() => Disposed = true;

        public void RaiseLink(bool up) => LinkChanged?.Invoke(up);

        public void RaiseDevice(SkalarkiDeviceId device, bool connected) => DeviceChanged?.Invoke(device, connected);

        public void RaiseEvent(SkalarkiHardwareEvent hardwareEvent) => HardwareEvent?.Invoke(hardwareEvent);
    }
}
