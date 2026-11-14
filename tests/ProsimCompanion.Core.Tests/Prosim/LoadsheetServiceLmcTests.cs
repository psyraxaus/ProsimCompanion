using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Aircraft.Gateway;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.State;
using ProsimCompanion.Core.Tests.TechLog;
using ProsimCompanion.Prosim.Acars;
using ProsimCompanion.Prosim.Loadsheet;
using Xunit;

namespace ProsimCompanion.Core.Tests.Prosim;

/// <summary>Issue #181, the fault of 2026-11-14: the automatic last-minute-change check hung
/// off the boarding-complete edge, the same edge that only schedules the final, and never ran.
/// The whole sequence through the real <see cref="LoadsheetService"/> over a fake aircraft:
/// the figures are the flight's own (FINAL EDNO 1: ZFW 54945, TOW 62245, pax 106, fuel 7300).</summary>
public sealed class LoadsheetServiceLmcTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pc-lmc-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, object> _aircraft = new(StringComparer.Ordinal)
    {
        [ProsimDataRefNames.PaxZone1Capacity.Name] = 12,
        [ProsimDataRefNames.PaxZone2Capacity.Name] = 54,
        [ProsimDataRefNames.PaxZone3Capacity.Name] = 60,
        [ProsimDataRefNames.PaxZone4Capacity.Name] = 60,
        [ProsimDataRefNames.PaxZone1Amount.Name] = 10,
        [ProsimDataRefNames.PaxZone2Amount.Name] = 30,
        [ProsimDataRefNames.PaxZone3Amount.Name] = 33,
        [ProsimDataRefNames.PaxZone4Amount.Name] = 33,
        [ProsimDataRefNames.CargoForwardAmount.Name] = 800.0,
        [ProsimDataRefNames.CargoAftAmount.Name] = 1200.0,
        [ProsimDataRefNames.CargoForwardCapacity.Name] = 3400.0,
        [ProsimDataRefNames.CargoAftCapacity.Name] = 4500.0,
        [ProsimDataRefNames.CargoBulkCapacity.Name] = 1500.0,
        [ProsimDataRefNames.FuelCenter.Name] = 0.0,
        [ProsimDataRefNames.FuelLeft.Name] = 3650.0,
        [ProsimDataRefNames.FuelRight.Name] = 3650.0,
        [ProsimDataRefNames.WeightZfw.Name] = 54945.0,
        [ProsimDataRefNames.WeightZfwMax.Name] = 73800.0,
        [ProsimDataRefNames.WeightGross.Name] = 62245.0,
        [ProsimDataRefNames.WeightGrossMax.Name] = 93500.0,
        [ProsimDataRefNames.CenterOfGravity.Name] = 25.6,
        [ProsimDataRefNames.Zfwcg.Name] = 27.5,
    };

    private readonly FlightDataOptions _options = new()
    {
        LmcEnabled = true,
        AutoPrelimOnRefuel = false,
        // No crew-realism wait in a test: the final follows boarding complete at once.
        FinalDelayMinSeconds = 0,
        FinalDelayMaxSeconds = 0,
    };

    private readonly GroundOpsSignals _signals = new();
    private readonly LoadsheetStore _store = new();
    private readonly GsxDiagnosticsStore _diagnostics = new();
    private readonly List<string> _uplinks = [];
    private JsonlEventLog _eventLog = null!;
    private LmcRevisionStore _question = null!;
    private LoadsheetService _service = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _eventLog = new JsonlEventLog(_dir, NullLogger<JsonlEventLog>.Instance);
        _question = new LmcRevisionStore(_signals);

        var prosim = new Mock<IProsimDataRefs>();
        prosim
            .Setup(p => p.SubscribeDynamic(It.IsAny<string>(), It.IsAny<DataRefTier>()))
            .Returns<string, DataRefTier>((name, _) => new FakeSubscription(name, _aircraft));

        var gateway = new Mock<IProsimGateway>();
        gateway
            .Setup(g => g.WriteDataRefAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Callback<string, object, CancellationToken>((name, value, _) =>
            {
                if (name == ProsimDataRefNames.AocMessageUplink)
                {
                    lock (_uplinks)
                    {
                        _uplinks.Add(value.ToString() ?? "");
                    }
                }
            })
            .ReturnsAsync(true);

        var ofp = new OfpStore();
        ofp.Set(new OfpData
        {
            FlightNumber = "BAW700", Callsign = "BAW700", AircraftReg = "G-EUYB", OriginIcao = "EGLL", DestinationIcao = "LOWW",
            PaxCount = 106, CargoKg = 2000, FuelPlanRampKg = 7300, FuelPlanLandingKg = 2900, FuelTaxiKg = 200,
            EstZfwKg = 54779, EstTowKg = 61780, EstLdwKg = 57380,
        });

        var clock = new Mock<ISimClock>();
        clock.SetupGet(c => c.UtcNowOrReal).Returns(() => DateTimeOffset.UtcNow);
        var overrides = new Mock<IEfbInitOverrides>();
        overrides.Setup(o => o.Snapshot()).Returns(new Dictionary<string, double>());

        _service = new LoadsheetService(
            prosim.Object, gateway.Object, new AcarsUplink(gateway.Object, NullLogger<AcarsUplink>.Instance),
            ofp, _store, _signals, new ConnectionStatusStore(), Mock.Of<IFmsInitSync>(),
            OptionsSupport.Monitor(_options), _diagnostics, new GsxResyncState(), Mock.Of<ISimVars>(),
            clock.Object, overrides.Object, NullLogger<LoadsheetService>.Instance, _question, _eventLog)
        {
            SettleDelay = TimeSpan.Zero,
        };
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _service.Dispose();
        _question.Dispose();
        await _eventLog.DisposeAsync();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder left behind is no test failure.
        }
    }

    [Fact]
    public async Task StandardOrder_BoardingComplete_DelayedFinal_ThenDoorsClosed_EvaluatesOnceAtDoorsClosed()
    {
        Assert.True(await _service.GeneratePreliminaryAsync());

        // 2026-11-14 08:16:37 local: boarding complete, the final is only scheduled.
        _signals.RaiseBoardingCompleted();
        await Until(() => _store.Snapshot().Final.Status == LoadsheetSlotStatus.Sent);
        Assert.Contains(Decisions(), d => d == "boarding complete — no final loadsheet yet, last-minute change check waits for doors closed / pushback");
        Assert.DoesNotContain(Decisions(), d => d.StartsWith("last-minute change check at", StringComparison.Ordinal));
        Assert.Null(_store.Snapshot().Lmc);

        // Two passengers offloaded after the final, then the doors close.
        MovePassengers(-2);
        _signals.RaisePushbackProgress(PushbackStage.DoorsClosed);
        await Until(() => _store.Snapshot().Lmc is not null);

        var lmc = _store.Snapshot().Lmc!;
        Assert.Equal(LmcDisposition.Applied, lmc.Disposition);
        Assert.Equal(Core.Aircraft.WeightAndBalance.LmcVerdict.WithinTolerance, lmc.Result.Verdict);
        Assert.Equal(-2, lmc.Result.DeltaPax);
        Assert.Equal(-176, lmc.Result.DeltaZfwKg, 1);
        Assert.Contains(Decisions(), d => d == "last-minute change check at pushback stage DoorsClosed — the load aboard against the final loadsheet");
        await Until(() => Uplinks().Any(u => u.Contains("LAST MINUTE CHANGE", StringComparison.Ordinal)));

        // Never twice per cycle: more passengers gone, the later stages say nothing.
        MovePassengers(-3);
        _signals.RaisePushbackProgress(PushbackStage.BridgeRemoved);
        _signals.RaisePushbackProgress(PushbackStage.TugConnected);
        _signals.RaisePushbackProgress(PushbackStage.Pushing);
        await Task.Delay(300);

        Assert.Single(Decisions(), d => d.StartsWith("last-minute change check at", StringComparison.Ordinal));
        Assert.Single(Decisions(), d => d.StartsWith("last-minute change WithinTolerance", StringComparison.Ordinal));
        Assert.Equal(-2, _store.Snapshot().Lmc!.Result.DeltaPax);
        Assert.Single(Uplinks(), u => u.Contains("LAST MINUTE CHANGE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StandardOrder_NothingChangedAfterTheFinal_IsEvaluatedAndSilent()
    {
        Assert.True(await _service.GeneratePreliminaryAsync());
        _signals.RaiseBoardingCompleted();
        await Until(() => _store.Snapshot().Final.Status == LoadsheetSlotStatus.Sent);

        // A pushback asked for on GSX's own menu: no doors-closed stage, the tug is the first edge.
        _signals.RaisePushbackProgress(PushbackStage.TugConnected);
        await Until(() => Decisions().Any(d => d.StartsWith("last-minute change NoChange", StringComparison.Ordinal)));

        Assert.Contains(Decisions(), d => d.StartsWith("last-minute change check at pushback stage TugConnected", StringComparison.Ordinal));
        Assert.Null(_store.Snapshot().Lmc);
        Assert.DoesNotContain(Uplinks(), u => u.Contains("LAST MINUTE CHANGE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StandardOrder_FuelBurntAtTheStandSinceTheFinal_IsNotALastMinuteChange()
    {
        Assert.True(await _service.GeneratePreliminaryAsync());
        _signals.RaiseBoardingCompleted();
        await Until(() => _store.Snapshot().Final.Status == LoadsheetSlotStatus.Sent);

        // 40 minutes of APU: 80 kg gone from the tanks and the gross weight, payload untouched.
        _aircraft[ProsimDataRefNames.FuelLeft.Name] = 3610.0;
        _aircraft[ProsimDataRefNames.FuelRight.Name] = 3610.0;
        _aircraft[ProsimDataRefNames.WeightGross.Name] = 62165.0;
        _signals.RaisePushbackProgress(PushbackStage.DoorsClosed);
        await Until(() => Decisions().Any(d => d.Contains("burnt at the stand, not a change", StringComparison.Ordinal)));

        Assert.Null(_store.Snapshot().Lmc);
        Assert.DoesNotContain(Uplinks(), u => u.Contains("LAST MINUTE CHANGE", StringComparison.Ordinal));

        // On demand the same difference is shown as measured — that path is unchanged.
        Assert.True(await _service.EvaluateLastMinuteChangeAsync());
        Assert.Equal(-80, _store.Snapshot().Lmc!.Result.DeltaFuelKg, 1);
    }

    [Fact]
    public async Task ManualFinalBeforeBoardingEnds_EvaluatesAtBoardingComplete_AndNotAgainAtThePushback()
    {
        Assert.True(await _service.GeneratePreliminaryAsync());
        MovePassengers(-2); // two still on the jetway when the captain asks for the final
        Assert.True(await _service.GenerateFinalAsync());
        Assert.Equal(104, _store.Snapshot().Final.Pax);

        MovePassengers(+2);
        _signals.RaiseBoardingCompleted();
        await Until(() => _store.Snapshot().Lmc is not null);

        Assert.Equal(2, _store.Snapshot().Lmc!.Result.DeltaPax);
        Assert.Equal(LmcDisposition.Applied, _store.Snapshot().Lmc!.Disposition);
        Assert.Contains(Decisions(), d => d == "last-minute change check at boarding complete — the final was issued before boarding ended");
        Assert.DoesNotContain(Decisions(), d => d.Contains("final loadsheet in", StringComparison.Ordinal)); // no second final

        MovePassengers(-1);
        _signals.RaisePushbackProgress(PushbackStage.DoorsClosed);
        _signals.RaisePushbackProgress(PushbackStage.Pushing);
        await Task.Delay(300);

        Assert.Single(Decisions(), d => d.StartsWith("last-minute change check at", StringComparison.Ordinal));
        Assert.Equal(2, _store.Snapshot().Lmc!.Result.DeltaPax);
    }

    [Fact]
    public async Task SwitchOff_NeitherEdgeEvaluatesOrSaysAnything()
    {
        _options.LmcEnabled = false;
        Assert.True(await _service.GeneratePreliminaryAsync());
        _signals.RaiseBoardingCompleted();
        await Until(() => _store.Snapshot().Final.Status == LoadsheetSlotStatus.Sent);

        MovePassengers(-2);
        _signals.RaisePushbackProgress(PushbackStage.DoorsClosed);
        _signals.RaisePushbackProgress(PushbackStage.Pushing);
        await Task.Delay(300);

        Assert.DoesNotContain(Decisions(), d => d.Contains("last-minute change", StringComparison.Ordinal));
        Assert.Null(_store.Snapshot().Lmc);
    }

    [Fact]
    public async Task ANewDepartureCycle_GetsItsOwnEvaluation()
    {
        Assert.True(await _service.GeneratePreliminaryAsync());
        _signals.RaiseBoardingCompleted();
        await Until(() => _store.Snapshot().Final.Status == LoadsheetSlotStatus.Sent);
        _signals.RaisePushbackProgress(PushbackStage.DoorsClosed);
        await Until(() => Decisions().Count(d => d.StartsWith("last-minute change NoChange", StringComparison.Ordinal)) == 1);

        _signals.RaiseFlightCycleReset();
        Assert.True(await _service.GeneratePreliminaryAsync());
        _signals.RaiseBoardingCompleted();
        await Until(() => _store.Snapshot().Final.Status == LoadsheetSlotStatus.Sent);
        _signals.RaisePushbackProgress(PushbackStage.DoorsClosed);
        await Until(() => Decisions().Count(d => d.StartsWith("last-minute change NoChange", StringComparison.Ordinal)) == 2);
    }

    /// <summary>Passengers on or off zone 4 at 88 kg each, as ProSim's own weights follow them.</summary>
    private void MovePassengers(int delta)
    {
        _aircraft[ProsimDataRefNames.PaxZone4Amount.Name] = (int)_aircraft[ProsimDataRefNames.PaxZone4Amount.Name] + delta;
        _aircraft[ProsimDataRefNames.WeightZfw.Name] = (double)_aircraft[ProsimDataRefNames.WeightZfw.Name] + (delta * 88.0);
        _aircraft[ProsimDataRefNames.WeightGross.Name] = (double)_aircraft[ProsimDataRefNames.WeightGross.Name] + (delta * 88.0);
    }

    private List<string> Decisions()
        => [.. _diagnostics.Snapshot().RecentDecisions.Where(d => d.Action == "loadsheet").Select(d => d.Reason)];

    private List<string> Uplinks()
    {
        lock (_uplinks)
        {
            return [.. _uplinks];
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "the expected state was not reached within 10 s");
            await Task.Delay(20);
        }
    }

    private sealed class FakeSubscription(string name, Dictionary<string, object> aircraft) : IDataRefSubscription
    {
        public string Name { get; } = name;

        public object? RawValue => aircraft.TryGetValue(Name, out var value) ? value : null;

        public bool IsStale => false;

        public DateTimeOffset? LastUpdatedUtc => DateTimeOffset.UtcNow;

#pragma warning disable CS0067 // the fake never pushes
        public event EventHandler? ValueChanged;
#pragma warning restore CS0067

        public T GetValue<T>(T fallback)
            => RawValue is { } value ? (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture) : fallback;

        public void Dispose()
        {
        }
    }
}
