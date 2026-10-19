using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Core.Tests.Flight;
using ProsimCompanion.Speech.Cabin;
using ProsimCompanion.Speech.Playback;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>
/// The cabin-crew shell through its seams (C4): the beacon is the engine's committed ground
/// fact, the phase the phase source, the doors ProSim's — the tick is driven directly, no
/// timer. The first tests this module has had; they cover the paths the refactor touched.
/// </summary>
public sealed class CabinCrewServiceTests : IDisposable
{
    private readonly FakeDataRefs _dataRefs = new();
    private readonly FakeFlightSource _flightData = new()
    {
        Snapshot = new FlightDataSnapshot { IsValid = true, IsReady = true, OnGround = true },
    };
    private readonly FakePhaseSource _flight = new();
    private readonly FakeGroundFactsSource _groundFacts = new();
    private readonly FakeArbiter _arbiter = new();
    private readonly SpeechStatusStore _store = new();
    private readonly GroundOpsSignals _signals = new();
    private readonly CabinOptions _options = new()
    {
        Enabled = true,
        CabinSecure = true,
        CabinSecureMinDelaySeconds = 0,
        CabinSecureSecondsPerPax = 0,
        CabinReady = false,
        RequireCabChannel = false,
        Chime = false,
        FoAcknowledge = false,
        AmbientEvents = false,
        CruiseQuery = false,
    };
    private readonly CabinCrewService _service;
    private int _secured;

    public CabinCrewServiceTests()
    {
        var options = new Mock<IOptionsMonitor<CabinOptions>>();
        options.SetupGet(o => o.CurrentValue).Returns(() => _options);
        var playback = new Mock<ISpeechPlayback>();
        playback.Setup(p => p.PlayChimeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _signals.CabinSecured += () => _secured++;

        _service = new CabinCrewService(
            _dataRefs, _flightData, _flight, _groundFacts, _arbiter, playback.Object, options.Object,
            SpeechTestSupport.TempEventLog(), _store, new GsxDiagnosticsStore(), new OfpStore(),
            NullLogger<CabinCrewService>.Instance, _signals);
        _service.Attach();
        _flight.SetPhase(FlightPhase.PushbackAndStart);
    }

    public void Dispose() => _service.Dispose();

    private void WaitForReport(string tag)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!_arbiter.Tags.Contains(tag) && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(10);
        }
    }

    [Fact]
    public void CommittedBeaconOn_DoorsClosed_AtPushback_ReportsCabinSecure()
    {
        _groundFacts.Facts = GroundFacts.None with { BeaconOn = true };

        _service.Tick();
        WaitForReport("cabin.secure");

        Assert.Equal(CabinSecureState.Secure, _store.Snapshot().CabinSecure);
        Assert.Equal(1, _secured);
        Assert.Contains("cabin.secure", _arbiter.Tags);
    }

    [Fact]
    public void CommittedBeaconOff_NoReport()
    {
        _groundFacts.Facts = GroundFacts.None with { BeaconOn = false };

        _service.Tick();
        _service.Tick();

        Assert.Equal(CabinSecureState.None, _store.Snapshot().CabinSecure);
        Assert.Empty(_arbiter.Requests);
        Assert.Equal(0, _secured);
    }

    [Fact]
    public void TheBeaconFactAloneDecides_NotTheSampleField()
    {
        // The flight-data sample carries a raw BeaconOn as well; the cabin reads the
        // committed fact. A sample saying "on" with the fact "off" is not a secure cabin.
        _flightData.Snapshot = _flightData.Snapshot with { BeaconOn = true };
        _groundFacts.Facts = GroundFacts.None with { BeaconOn = false };

        _service.Tick();

        Assert.Equal(CabinSecureState.None, _store.Snapshot().CabinSecure);
        Assert.Empty(_arbiter.Requests);
    }

    [Fact]
    public void NotLive_HoldsEvenWithTheBeaconOn()
    {
        // Issue #114: ProSim pushes plausible data with no MSFS session; the live gate holds.
        _flight.SetLive(false);
        _groundFacts.Facts = GroundFacts.None with { BeaconOn = true };

        _service.Tick();

        Assert.Equal(CabinSecureState.None, _store.Snapshot().CabinSecure);
        Assert.Empty(_arbiter.Requests);
    }

    [Fact]
    public void OpenDoor_HoldsTheReport()
    {
        _dataRefs.Values[ProsimCompanion.Core.Aircraft.ProsimDataRefNames.Door1L.Name] = true;
        _groundFacts.Facts = GroundFacts.None with { BeaconOn = true };

        _service.Tick();

        Assert.Equal(CabinSecureState.None, _store.Snapshot().CabinSecure);
        Assert.Empty(_arbiter.Requests);
    }

    [Fact]
    public void WithADelay_TheFirstTickArmsAndPublishesSecuring()
    {
        // Issue #134: the report waits a pax-scaled while; the pill reads "securing" meanwhile.
        _options.CabinSecureMinDelaySeconds = 60;
        _groundFacts.Facts = GroundFacts.None with { BeaconOn = true };

        _service.Tick();
        _service.Tick();

        Assert.Equal(CabinSecureState.Securing, _store.Snapshot().CabinSecure);
        Assert.Empty(_arbiter.Requests);
        Assert.NotNull(_service.Capture()); // the armed timer is snapshot state (#167)
    }

    [Fact]
    public void Turnaround_RearmsTheReport()
    {
        _groundFacts.Facts = GroundFacts.None with { BeaconOn = true };
        _service.Tick();
        WaitForReport("cabin.secure");
        Assert.Equal(CabinSecureState.Secure, _store.Snapshot().CabinSecure);

        // Same leg: no second report however often the tick runs.
        _service.Tick();
        Assert.Single(_arbiter.Requests);

        // Shutdown, then the next leg's Preflight: the core re-arms and the pill clears.
        _flight.SetPhase(FlightPhase.TaxiIn);
        _flight.SetPhase(FlightPhase.Shutdown);
        _groundFacts.Facts = GroundFacts.None;
        _flight.SetPhase(FlightPhase.Preflight);
        Assert.Equal(CabinSecureState.None, _store.Snapshot().CabinSecure);

        _flight.SetPhase(FlightPhase.PushbackAndStart);
        _groundFacts.Facts = GroundFacts.None with { BeaconOn = true };
        _service.Tick();
        WaitForReport("cabin.secure");
        Assert.Equal(2, _arbiter.Requests.Count);
        Assert.Equal(2, _secured);
    }
}
