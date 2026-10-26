using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Diagnostics.Ecam;
using ProsimCompanion.Core.Diagnostics.Metrics;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Prosim.DataRefs;
using ProsimCompanion.Prosim.Diagnostics;
using ProsimCompanion.Sim;
using ProsimCompanion.Sim.Diagnostics;
using Xunit;

namespace ProsimCompanion.Core.Tests.Diagnostics.Ecam;

/// <summary>The two SIM-page collectors (ProSim and SimConnect) over faked stores: connected,
/// reconnecting (caution + HOLD LAST), and nothing-known (XX); the dispatch-rate hook; the
/// door reader; the subscription stats.</summary>
public sealed class SimPageCollectorTests : IAsyncDisposable
{
    private readonly ConnectionStatusStore _connections = new();
    private readonly RateRegistry _rates = new();
    private readonly ProsimDataRefService _dataRefs;
    private readonly TableDataRefs _doorsFeed = new();
    private readonly Mock<IFlightPhaseSource> _flight = new();

    public SimPageCollectorTests()
    {
        _dataRefs = new ProsimDataRefService(
            new FixedOptionsMonitor<ProsimOptions>(new ProsimOptions()), NullLogger<ProsimDataRefService>.Instance, _rates);
        _flight.SetupGet(f => f.CurrentPhase).Returns(FlightPhase.Unknown);
        _flight.Setup(f => f.Snapshot()).Returns(new FlightStateView(FlightPhase.Unknown, null, false));
    }

    public ValueTask DisposeAsync() => _dataRefs.DisposeAsync();

    private ProsimDiagnosticsCollector Prosim(DoorStateReader? doors = null)
        => new(_connections, new ProsimSetupStore(), _dataRefs, _rates, _flight.Object, doors ?? new DoorStateReader(_doorsFeed));

    [Fact]
    public void Prosim_NothingKnown_IsNoDataAndNotApplicable()
    {
        var (page, cautions, _) = CollectorTestSupport.Run(Prosim());

        Assert.Equal(DiagState.NotApplicable, page.Nodes["prosim"].State);   // dot never set = Disabled
        Assert.Equal(DiagState.NoData, page.Nodes["phase"].State);
        Assert.Equal(DiagState.NoData, page.Nodes["ground"].State);
        Assert.Equal(DiagState.NoData, page.Nodes["doors"].State);
        Assert.Equal("0", page.Readout("subs_datarefs"));
        Assert.Empty(cautions.Cautions);
    }

    [Fact]
    public void Prosim_Connected_ShowsVersionGridAndBus()
    {
        _connections.Set(Subsystems.Prosim, ConnectionState.Connected);
        _flight.SetupGet(f => f.CurrentPhase).Returns(FlightPhase.Preflight);
        _flight.Setup(f => f.Snapshot()).Returns(new FlightStateView(
            FlightPhase.Preflight,
            new FlightDataSnapshot { OnGround = true, ParkBrakeSet = true, ExternalPowerOn = true, AircraftPowered = true, IsValid = true },
            false, IsLive: true));
        _doorsFeed.Push(ProsimDataRefNames.Door1L.Name, true);
        _doorsFeed.Push(ProsimDataRefNames.DoorCargoForward.Name, false);

        var doors = new DoorStateReader(_doorsFeed);
        // Subscribe first so the pushes land on registered names.
        _ = doors.Read();
        _doorsFeed.Push(ProsimDataRefNames.Door1L.Name, true);
        _doorsFeed.Push(ProsimDataRefNames.DoorCargoForward.Name, false);

        var (page, cautions, _) = CollectorTestSupport.Run(Prosim(doors));

        Assert.Equal(DiagState.Normal, page.Nodes["prosim"].State);
        Assert.Equal(EcamTokens.Conn, page.Nodes["prosim"].Value);
        Assert.Equal("PREFLIGHT", page.Nodes["phase"].Value);
        Assert.Equal(EcamTokens.OnGround, page.Nodes["ground"].Value);
        Assert.Equal("PARK BRK SET", page.Nodes["ground"].Detail);
        Assert.Equal("EXT PWR", page.Nodes["power"].Value);
        Assert.Equal("1L OPEN", page.Nodes["doors"].Value);
        Assert.Equal("CARGO CLSD", page.Nodes["doors"].Detail);
        Assert.Equal(DiagState.Normal, page.Nodes["dataref_bus"].State);
        Assert.Equal(DiagState.Normal, page.LinkState("prosim", "dataref_bus"));
        Assert.Empty(cautions.Cautions);
    }

    [Fact]
    public void Prosim_Reconnecting_IsACautionWithHoldLast()
    {
        _connections.Set(Subsystems.Prosim, ConnectionState.Disconnected);
        var (page, cautions, log) = CollectorTestSupport.Run(Prosim());

        Assert.Equal(EcamTokens.Reconn, page.Nodes["prosim"].Value);
        Assert.Equal(DiagState.Caution, page.Nodes["prosim"].State);
        var caution = Assert.Single(cautions.Cautions);
        Assert.Equal("PROSIM LOST", caution.Title);
        Assert.Contains(caution.Actions, a => a.Value == EcamTokens.HoldLast);
        Assert.Contains("DATAREF FEED", cautions.DegradedSystems);
        Assert.Contains(log.Snapshot(), e => e.Text == "PROSIM LOST");
    }

    [Fact]
    public void DataRefService_CountsDispatchesAndStats()
    {
        using var sub = _dataRefs.SubscribeDynamic("aircraft.test", DataRefTier.Normal);
        Assert.Equal(1, _dataRefs.Count);
        Assert.Equal(1, _dataRefs.Stale);   // registered, nothing arrived yet

        // The push lands on the dispatcher task: wait for both the stale flag and the rate hit
        // (under a full-suite load the task can take a moment to be scheduled).
        _dataRefs.UpdateFromPush("aircraft.test", 1.0);
        SpinWait.SpinUntil(() => _dataRefs.Stale == 0 && _rates[RateNames.Datarefs].PerSecond > 0, TimeSpan.FromSeconds(10));
        Assert.Equal(0, _dataRefs.Stale);
        Assert.True(_rates[RateNames.Datarefs].PerSecond > 0);
    }

    [Fact]
    public void SimConnect_Connected_And_Lost()
    {
        var session = new SimSessionStore();
        var simVars = new SimVarService(NullLogger<SimVarService>.Instance, _rates);
        var collector = new SimConnectDiagnosticsCollector(_connections, session, simVars, _rates);

        var (empty, _, _) = CollectorTestSupport.Run(collector);
        Assert.Equal(DiagState.NotApplicable, empty.Nodes["simconnect"].State);

        _connections.Set(Subsystems.SimConnect, ConnectionState.Connected);
        session.Publish(new SimSessionSnapshot(SimSessionPhase.InSession, true, false, 2, "KittyHawk 12.1"));
        using var sub = simVars.SubscribeDynamic("PLANE ALTITUDE", "feet", DataRefTier.Normal);
        simVars.UpdateFromSim("PLANE ALTITUDE", 1200);
        var (ok, okCautions, _) = CollectorTestSupport.Run(collector);
        Assert.Equal(EcamTokens.Conn, ok.Nodes["simconnect"].Value);
        Assert.Equal("KITTYHAWK 12.1", ok.Nodes["simconnect"].Detail);
        Assert.Equal("IN SESSION", ok.Readout("sim_session"));
        Assert.Equal("1", ok.Readout("subs_simvars"));
        Assert.Equal("0", ok.Readout("stale_simvars"));
        Assert.Empty(okCautions.Cautions);
        Assert.True(_rates[RateNames.Simvars].PerSecond > 0);

        _connections.Set(Subsystems.SimConnect, ConnectionState.Connecting);
        var (lost, lostCautions, _) = CollectorTestSupport.Run(collector);
        Assert.Equal(DiagState.Caution, lost.Nodes["simvar_bus"].State);
        Assert.Single(lostCautions.Cautions, c => c.Title == "SIMCONNECT LOST");
        Assert.Contains("SIMVAR FEED", lostCautions.DegradedSystems);
        Assert.Equal("SIMCONNECT LOST · RECONNECT AUTO", lost.CautionLine);
    }

    [Fact]
    public void BusState_AllStaleWhileConnected_IsAmber()
    {
        Assert.Equal(DiagState.Caution, ProsimDiagnosticsCollector.BusState(ConnectionState.Connected, 5, 5));
        Assert.Equal(DiagState.Normal, ProsimDiagnosticsCollector.BusState(ConnectionState.Connected, 1, 5));
        Assert.Equal(DiagState.NotApplicable, ProsimDiagnosticsCollector.BusState(ConnectionState.Disabled, 0, 0));
        Assert.Equal(DiagState.Caution, SimConnectDiagnosticsCollector.BusState(ConnectionState.Disconnected, 0, 3));
    }
}
