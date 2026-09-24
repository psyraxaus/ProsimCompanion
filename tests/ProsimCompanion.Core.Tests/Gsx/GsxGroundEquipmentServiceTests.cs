using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Aircraft.Gateway;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Core.Tests.Speech;
using ProsimCompanion.Gsx.Sync;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

/// <summary>Gradual-removal GPU latch (2026-09-25 cold-and-dark report): the GPU is pulled
/// only on a falling edge of external power, once per removal, and the latch starts fresh
/// on shutdown and on re-placement.</summary>
public sealed class GsxGroundEquipmentServiceTests
{
    private readonly FakeDataRefs _dataRefs = new();
    private readonly Mock<IProsimGateway> _gateway = new();
    private readonly Mock<IFlightPhaseSource> _flight = new();
    private readonly GsxDiagnosticsStore _diagnostics = new();
    private readonly List<(string Name, object Value)> _writes = [];
    private readonly GsxOptions _options = new()
    {
        AutomationEnabled = true,
        AutoGroundEquipment = true,
        GradualGroundEquipRemoval = true,
        BeaconPushbackSequenceEnabled = false,
        PcaMode = "never",
    };

    private GsxGroundEquipmentService CreateService()
    {
        _gateway
            .Setup(g => g.WriteDataRefAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Callback<string, object, CancellationToken>((name, value, _) => _writes.Add((name, value)))
            .ReturnsAsync(true);

        var jetway = new Mock<IDataRefSubscription>();
        jetway.Setup(s => s.GetValue(It.IsAny<double>())).Returns(0.0);
        var simVars = new Mock<ISimVars>();
        simVars
            .Setup(s => s.SubscribeDynamic(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DataRefTier>()))
            .Returns(jetway.Object);

        var options = new Mock<IOptionsMonitor<GsxOptions>>();
        options.SetupGet(o => o.CurrentValue).Returns(() => _options);
        _flight.SetupGet(f => f.CurrentPhase).Returns(FlightPhase.Preflight);

        var writer = new GsxProsimWriter(_gateway.Object, _diagnostics, NullLogger<GsxProsimWriter>.Instance);
        return new GsxGroundEquipmentService(
            _dataRefs,
            simVars.Object,
            writer,
            _flight.Object,
            options.Object,
            _diagnostics,
            NullLogger<GsxGroundEquipmentService>.Instance);
    }

    private void SetGpu(bool connected) => _dataRefs.Values[ProsimDataRefNames.GroundPower.Name] = connected;

    private void SetExternalPower(bool on) => _dataRefs.Values[ProsimDataRefNames.ElecExternalConnect.Name] = on;

    private int GpuOffWrites()
        => _writes.Count(w => w.Name == ProsimDataRefNames.GroundPower.Name && w.Value is false);

    private void Shutdown()
        => _flight.Raise(f => f.PhaseChanged += null,
            new FlightPhaseChangedEventArgs(FlightPhase.TaxiIn, FlightPhase.Shutdown));

    [Fact]
    public async Task GpuConnected_ExternalPowerNeverSeen_IsNotRemoved()
    {
        var service = CreateService();
        SetGpu(true);
        SetExternalPower(false);

        for (var i = 0; i < 5; i++)
        {
            await service.TickGradualRemovalAsync();
        }

        Assert.Equal(0, GpuOffWrites());
    }

    [Fact]
    public async Task ExternalPowerSeenThenDropped_RemovesGpuExactlyOnce()
    {
        var service = CreateService();
        SetGpu(true);
        SetExternalPower(true);
        await service.TickGradualRemovalAsync();
        Assert.Equal(0, GpuOffWrites());

        // Crew lets go of external power; the ProSim echo of the GPU write lags, so the GPU
        // still reads connected for several ticks.
        SetExternalPower(false);
        for (var i = 0; i < 4; i++)
        {
            await service.TickGradualRemovalAsync();
        }

        Assert.Equal(1, GpuOffWrites());
        Assert.Single(_diagnostics.Snapshot().RecentDecisions,
            d => d.Reason.Contains("GPU off", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Shutdown_ResetsTheLatch()
    {
        var service = CreateService();
        SetGpu(true);
        SetExternalPower(true);
        await service.TickGradualRemovalAsync();

        Shutdown();

        // Next turnaround: GPU present, external power never re-seen → nothing removed.
        SetExternalPower(false);
        for (var i = 0; i < 3; i++)
        {
            await service.TickGradualRemovalAsync();
        }

        Assert.Equal(0, GpuOffWrites());
    }

    [Fact]
    public async Task Replacement_ResetsTheLatch()
    {
        var service = CreateService();

        // External power was seen before placement (a stale reading from earlier) …
        SetGpu(false);
        SetExternalPower(true);
        await service.TickGradualRemovalAsync();

        // … then ground prep places the GPU: the latch must start fresh here.
        Assert.Equal(GsxPrepStatus.Done, await service.RunPlacementStepAsync());
        Assert.Contains(_writes, w => w.Name == ProsimDataRefNames.GroundPower.Name && w.Value is true);

        SetGpu(true);
        SetExternalPower(false);
        for (var i = 0; i < 3; i++)
        {
            await service.TickGradualRemovalAsync();
        }

        Assert.Equal(0, GpuOffWrites());
    }
}
