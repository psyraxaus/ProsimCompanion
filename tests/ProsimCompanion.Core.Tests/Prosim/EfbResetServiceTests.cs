using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Aircraft.Gateway;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Commands;
using ProsimCompanion.Core.Commands.Handlers;
using ProsimCompanion.Core.State;
using ProsimCompanion.Prosim.Loadsheet;
using Xunit;

namespace ProsimCompanion.Core.Tests.Prosim;

/// <summary>The INIT page's two resets (ProsimInterface parity, 2026-10-09): the soft reset
/// never writes the EFB; the full reset clears exactly the SimBrief import's write set plus
/// the two loadsheet slots, empties the OFP store and raises the turnaround signal.</summary>
public sealed class EfbResetServiceTests : IDisposable
{
    private readonly Speech.FakeDataRefs _refs = new();
    private readonly Mock<IProsimGateway> _gateway = new();
    private readonly OfpStore _ofp = new();
    private readonly GroundOpsSignals _signals = new();
    private readonly Mock<IEfbInitOverrides> _overrides = new();
    private readonly Mock<ILoadsheetControl> _loadsheets = new();
    private readonly List<(string Name, object Value)> _written = [];
    private readonly string _sessionsDir = Path.Combine(Path.GetTempPath(), "pc-efb-reset-" + Guid.NewGuid().ToString("N"));
    private readonly EfbResetService _service;

    public EfbResetServiceTests()
    {
        _gateway
            .Setup(g => g.WriteDataRefAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Callback<string, object, CancellationToken>((name, value, _) => _written.Add((name, value)))
            .ReturnsAsync(true);
        _overrides.Setup(o => o.ClearAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _overrides.Setup(o => o.Snapshot()).Returns(new Dictionary<string, double>());
        _refs.Values["aircraft.passengers.zone1.capacity"] = 2;
        _refs.Values["aircraft.passengers.zone2.capacity"] = 1;
        _refs.Values["aircraft.passengers.zone3.capacity"] = 1;
        _refs.Values["aircraft.passengers.zone4.capacity"] = 0;

        _service = new EfbResetService(
            _refs, _gateway.Object, _ofp, _signals, _overrides.Object, _loadsheets.Object,
            new Core.EventLog.JsonlEventLog(_sessionsDir, NullLogger<Core.EventLog.JsonlEventLog>.Instance),
            new GsxDiagnosticsStore(), NullLogger<EfbResetService>.Instance);
    }

    public void Dispose()
    {
        _service.Dispose();
        try
        {
            Directory.Delete(_sessionsDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Soft_ClearsOverridesAndLoadsheetCycle_WritesNothing_KeepsTheOfp()
    {
        _ofp.Set(new OfpData { RequestId = "abcd1234" });
        var raised = 0;
        _signals.FlightCycleReset += () => raised++;

        var result = await _service.ResetFlightAsync();

        Assert.Equal(EfbResetKind.Soft, result.Kind);
        Assert.True(result.Ok);
        Assert.Equal(["init-overrides", "loadsheet-cycle"], result.Cleared);
        _overrides.Verify(o => o.ClearAllAsync(It.IsAny<CancellationToken>()), Times.Once);
        _loadsheets.Verify(l => l.ResetCycle(), Times.Once);
        Assert.Empty(_written);
        Assert.NotNull(_ofp.Current);
        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task Full_ClearsExactlyTheImportWriteSet_PlusTheLoadsheetSlots()
    {
        _ofp.Set(new OfpData { RequestId = "abcd1234" });

        var result = await _service.UnloadOfpAsync();

        Assert.Equal(EfbResetKind.Full, result.Kind);
        Assert.True(result.Ok);
        var names = _written.Select(w => w.Name).ToArray();
        Assert.Equal(
        [
            "efb.passengers.booked.string",
            "efb.passengerStatistics",
            "aircraft.refuel.fuelTarget",
            "efb.plannedfuel",
            "efb.plannedCargoKg",
            "efb.simbriefPlanImported",
            "efb.prelimLoadsheet",
            "efb.finalLoadsheet",
        ], names);

        // Cleared values: an all-false seat map of the aircraft's capacity, zero statistics,
        // zero figures, the flag false, blank slots.
        Assert.Equal("false,false,false,false", _written[0].Value);
        Assert.Contains("\"Total\":0", (string)_written[1].Value, StringComparison.Ordinal);
        Assert.Equal(0.0, _written[2].Value);
        Assert.Equal(0.0, _written[3].Value);
        Assert.Equal(0.0, _written[4].Value);
        Assert.Equal(false, _written[5].Value);
        Assert.Equal("", _written[6].Value);
        Assert.Equal("", _written[7].Value);
    }

    [Fact]
    public async Task Full_EmptiesTheOfpStoreFirst_ThenRaisesTheTurnaroundSignal()
    {
        _ofp.Set(new OfpData { RequestId = "abcd1234" });
        OfpData? ofpAtFirstWrite = null;
        _gateway
            .Setup(g => g.WriteDataRefAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Callback<string, object, CancellationToken>((name, value, _) =>
            {
                ofpAtFirstWrite ??= _ofp.Current;
                _written.Add((name, value));
            })
            .ReturnsAsync(true);
        var raised = 0;
        _signals.FlightCycleReset += () => raised++;

        var result = await _service.UnloadOfpAsync();

        Assert.Null(_ofp.Current);
        Assert.Null(ofpAtFirstWrite);          // the store was already empty when the EFB writes began
        Assert.Equal(1, raised);
        Assert.Contains("ofp-store", result.Cleared);
        Assert.Contains("flight-cycle", result.Cleared);
    }

    [Fact]
    public async Task Full_ReportsWhatProsimRefused_AndStillResetsTheAppSide()
    {
        _gateway
            .Setup(g => g.WriteDataRefAsync("efb.simbriefPlanImported", It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var raised = 0;
        _signals.FlightCycleReset += () => raised++;

        var result = await _service.UnloadOfpAsync();

        Assert.False(result.Ok);
        Assert.Equal(["efb.simbriefPlanImported"], result.Failed);
        Assert.Equal(1, raised);

        var command = EfbCommandHandlers.FromResult(result);
        Assert.Equal(CommandOutcome.Failed, command.Outcome);
        Assert.Contains("efb.simbriefPlanImported", command.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Full_WithNoCapacityData_UsesTheA320FallbackSeatCount()
    {
        _refs.Values.Clear();

        await _service.UnloadOfpAsync();

        var seatMap = (string)_written[0].Value;
        Assert.Equal(24 + 30 + 36 + 42, seatMap.Split(',').Length);
        Assert.DoesNotContain("true", seatMap, StringComparison.Ordinal);
    }

    [Fact]
    public void Commands_AreRegistered_AndAnswerUnavailableWithoutTheSeam()
    {
        var registry = new CommandRegistry();
        EfbCommandHandlers.Register(registry, reset: null);

        Assert.Contains("efb.resetFlight", registry.Names);
        Assert.Contains("efb.unloadOfp", registry.Names);
    }
}
