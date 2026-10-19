using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Aircraft.Gateway;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Core.Tests.Flight;
using ProsimCompanion.Core.Tests.Speech;
using ProsimCompanion.Gsx.Sync;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

/// <summary>Gradual-removal GPU latch (2026-09-25 cold-and-dark report): the GPU is pulled
/// only on a falling edge of external power, once per removal, and the latch starts fresh
/// on shutdown and on re-placement. Since C4 the external power, the park brake and the
/// beacon edge come from the committed ground facts, so the tests drive those through the
/// <see cref="FakeGroundFactsSource"/> and only the ground-service datarefs through ProSim.</summary>
public sealed class GsxGroundEquipmentServiceTests
{
    private readonly FakeDataRefs _dataRefs = new();
    private readonly Mock<IProsimGateway> _gateway = new();
    private readonly Mock<IFlightPhaseSource> _flight = new();
    private readonly FakeGroundFactsSource _groundFacts = new();
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
            _groundFacts,
            options.Object,
            _diagnostics,
            NullLogger<GsxGroundEquipmentService>.Instance);
    }

    private void SetGpu(bool connected) => _dataRefs.Values[ProsimDataRefNames.GroundPower.Name] = connected;

    /// <summary>External power as the engine committed it (the raw dataref is no longer read here).</summary>
    private void SetExternalPower(bool on) => _groundFacts.Facts = _groundFacts.Facts with { ExternalPowerOn = on };

    private void SetParkBrake(bool set) => _groundFacts.Facts = _groundFacts.Facts with { ParkBrakeSet = set };

    private void SetChocks(bool placed) => _dataRefs.Values[ProsimDataRefNames.Chocks.Name] = placed;

    private int WritesOf(string name, bool value) => _writes.Count(w => w.Name == name && w.Value is bool b && b == value);

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

    // ---- the committed beacon edge (C4): removal fires on the engine's edge, not a raw push ----

    /// <summary>The removal is fire-and-forget off the edge; against the fake gateway it
    /// completes inline, but the wait keeps the assertion honest on a slow runner.</summary>
    private void WaitForWrites(int atLeast)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (_writes.Count < atLeast && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(10);
        }
    }

    [Fact]
    public void BeaconOnEdge_BeforeTaxiOut_RemovesEverythingOnce()
    {
        using var service = CreateService();
        SetGpu(true);
        SetChocks(true);
        SetParkBrake(true);

        _groundFacts.BeaconOn();
        WaitForWrites(3);

        Assert.Equal(1, WritesOf(ProsimDataRefNames.GroundPreconditionedAir.Name, false));
        Assert.Equal(1, WritesOf(ProsimDataRefNames.GroundPower.Name, false));
        Assert.Equal(1, WritesOf(ProsimDataRefNames.Chocks.Name, false));
        Assert.Contains(_diagnostics.Snapshot().RecentDecisions, d => d.Reason.StartsWith("beacon on — removing", StringComparison.Ordinal));

        // The crew toggles the beacon: the second edge of the session removes nothing again.
        _groundFacts.BeaconOff();
        _groundFacts.BeaconOn();
        Assert.Equal(3, _writes.Count);
    }

    [Fact]
    public void BeaconOnEdge_ParkBrakeNotSet_KeepsTheChocks_AndRetriesOnTheNextEdge()
    {
        using var service = CreateService();
        SetGpu(true);
        SetChocks(true);
        SetParkBrake(false);

        _groundFacts.BeaconOn();
        WaitForWrites(2);

        Assert.Equal(1, WritesOf(ProsimDataRefNames.GroundPower.Name, false));
        Assert.Equal(0, WritesOf(ProsimDataRefNames.Chocks.Name, false));
        Assert.Contains(_diagnostics.Snapshot().RecentDecisions, d => d.Reason == "chocks kept — park brake is not set");

        // Brake set, beacon cycled: the interlock lifts and the removal completes.
        SetParkBrake(true);
        _groundFacts.BeaconOff();
        _groundFacts.BeaconOn();
        WaitForWrites(5);
        Assert.Equal(1, WritesOf(ProsimDataRefNames.Chocks.Name, false));
    }

    [Fact]
    public void BeaconOnEdge_WithTheBeaconSequenceOn_LeavesRemovalToTheSequencer()
    {
        _options.BeaconPushbackSequenceEnabled = true;
        using var service = CreateService();
        SetGpu(true);
        SetChocks(true);
        SetParkBrake(true);

        _groundFacts.BeaconOn();

        Assert.Empty(_writes);
    }

    [Fact]
    public void BeaconOnEdge_AfterTaxiOut_DoesNothing()
    {
        using var service = CreateService();
        _flight.SetupGet(f => f.CurrentPhase).Returns(FlightPhase.TaxiOut);
        SetGpu(true);
        SetParkBrake(true);

        _groundFacts.BeaconOn();

        Assert.Empty(_writes);
    }

    [Fact]
    public void OtherEdges_NeverRemove()
    {
        using var service = CreateService();
        SetGpu(true);
        SetChocks(true);
        SetParkBrake(true);

        _groundFacts.BeaconOff();
        _groundFacts.Raise(GroundFactEdge.ParkBrakeSet, _groundFacts.Facts);
        _groundFacts.Raise(GroundFactEdge.ExternalPowerOff, _groundFacts.Facts);
        _groundFacts.Raise(GroundFactEdge.Parked, _groundFacts.Facts);

        Assert.Empty(_writes);
    }

    [Fact]
    public async Task GradualRemoval_ChocksFollowTheCommittedParkBrake()
    {
        using var service = CreateService();
        SetChocks(true);
        SetGpu(false);

        SetParkBrake(false);
        await service.TickGradualRemovalAsync();
        Assert.Equal(0, WritesOf(ProsimDataRefNames.Chocks.Name, false));

        SetParkBrake(true);
        await service.TickGradualRemovalAsync();
        Assert.Equal(1, WritesOf(ProsimDataRefNames.Chocks.Name, false));
    }

    [Fact]
    public async Task DisconnectGpu_IsInterlockedOnTheCommittedExternalPower()
    {
        using var service = CreateService();
        SetGpu(true);

        SetExternalPower(true);
        var (interlocked, _) = await service.DisconnectGpuAsync();
        Assert.Equal(GroundPowerResult.Interlocked, interlocked);
        Assert.Equal(0, GpuOffWrites());

        SetExternalPower(false);
        var (written, _) = await service.DisconnectGpuAsync();
        Assert.Equal(GroundPowerResult.Written, written);
        Assert.Equal(1, GpuOffWrites());
    }
}
