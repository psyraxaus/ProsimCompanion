using System.Globalization;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Boarding;
using ProsimCompanion.Core.Diagnostics.Ecam;
using ProsimCompanion.Core.Diagnostics.Metrics;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Prosim.DataRefs;

namespace ProsimCompanion.Prosim.Diagnostics;

/// <summary>
/// The SIM page's ProSim half: the PROSIM source box, the dataref bus figures, the aircraft
/// grid (phase, ground, power, doors) and the plan / load / weather rows. Everything comes
/// from stores the app already keeps; the only ProSim-specific knowledge is which dot and
/// which counters belong to this feed.
/// </summary>
public sealed class ProsimDiagnosticsCollector : IDiagnosticsCollector
{
    private const string System = "SIM";

    private readonly ConnectionStatusStore _connections;
    private readonly ProsimSetupStore _setup;
    private readonly ProsimDataRefService _dataRefs;
    private readonly RateRegistry _rates;
    private readonly IFlightPhaseSource _flight;
    private readonly DoorStateReader _doors;
    private readonly GateStatusStore? _gate;
    private readonly OfpStore? _ofp;
    private readonly TakeoffPerfStore? _takeoff;
    private readonly LoadsheetStore? _loadsheet;
    private readonly WeatherStore? _weather;
    private readonly StateTransitions<string> _edges = new();

    public ProsimDiagnosticsCollector(
        ConnectionStatusStore connections,
        ProsimSetupStore setup,
        ProsimDataRefService dataRefs,
        RateRegistry rates,
        IFlightPhaseSource flight,
        DoorStateReader doors,
        GateStatusStore? gate = null,
        OfpStore? ofp = null,
        TakeoffPerfStore? takeoff = null,
        LoadsheetStore? loadsheet = null,
        WeatherStore? weather = null)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(dataRefs);
        ArgumentNullException.ThrowIfNull(rates);
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(doors);
        _connections = connections;
        _setup = setup;
        _dataRefs = dataRefs;
        _rates = rates;
        _flight = flight;
        _doors = doors;
        _gate = gate;
        _ofp = ofp;
        _takeoff = takeoff;
        _loadsheet = loadsheet;
        _weather = weather;
    }

    public string Page => DiagPageIds.Sim;

    public void Contribute(DiagPageBuilder page, DiagCautionSink cautions)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(cautions);

        var state = _connections.Snapshot().FirstOrDefault(p => p.Key == Subsystems.Prosim).Value;
        var reason = _connections.ReasonOf(Subsystems.Prosim);
        var version = _setup.Snapshot().ProsimVersion;

        var (nodeState, value) = state switch
        {
            ConnectionState.Connected => (DiagState.Normal, EcamTokens.Conn),
            ConnectionState.Connecting => (DiagState.Caution, EcamTokens.Reconn),
            ConnectionState.Disconnected => (DiagState.Caution, EcamTokens.Reconn),
            _ => (DiagState.NotApplicable, EcamTokens.NotApplicable),
        };
        page.Node("prosim", nodeState, value, version is null ? (state == ConnectionState.Disabled ? "SDK OFF" : null) : version.ToUpperInvariant());

        var rate = _rates[RateNames.Datarefs].PerSecond;
        page.Readout("rate_datarefs", state == ConnectionState.Connected ? rate.ToString("0", CultureInfo.InvariantCulture) : null);
        page.Readout("subs_datarefs", _dataRefs.Count.ToString(CultureInfo.InvariantCulture));
        page.Readout("stale_datarefs", _dataRefs.Stale.ToString(CultureInfo.InvariantCulture));
        page.Node("dataref_bus", BusState(state, _dataRefs.Stale, _dataRefs.Count), "DATAREF BUS");
        page.Link("prosim", "dataref_bus");

        if (state is ConnectionState.Disconnected or ConnectionState.Connecting)
        {
            cautions.Raise(System, "PROSIM LOST", DiagState.Caution,
                new DiagAction("RECONNECT", EcamTokens.Auto),
                new DiagAction("DATAREFS", EcamTokens.HoldLast));
            cautions.Degraded("DATAREF FEED");
            page.CautionLine("PROSIM LOST · DATAREFS HOLD LAST");
        }
        else if (state == ConnectionState.Disabled && reason is not null)
        {
            cautions.Raise(System, "PROSIM SDK OFF", DiagState.Caution, new DiagAction("REASON", Short(reason)));
            cautions.Inop("AIRCRAFT DATA");
        }

        // Aircraft grid from the flight-state engine's last sample (the same sample the phase
        // engine classified, so the grid can never disagree with the phase pill).
        var view = _flight.Snapshot();
        var data = view.Data;
        var phaseText = DiagMemoSource.PhaseText(view.Phase);
        var gateId = _gate?.Snapshot().GateId;
        page.Node("phase", view.Phase == FlightPhase.Unknown ? DiagState.NoData : DiagState.Normal,
            view.Phase == FlightPhase.Unknown ? EcamTokens.NoData : phaseText,
            gateId is { Length: > 0 } ? $"GATE {gateId.ToUpperInvariant()}" : view.IsLive ? "LIVE" : "NOT LIVE");

        if (data is null || !data.IsValid)
        {
            page.NoData("ground").NoData("power");
        }
        else
        {
            page.Node("ground", DiagState.Normal, data.OnGround ? EcamTokens.OnGround : EcamTokens.Airborne,
                data.OnGround ? (data.ParkBrakeSet ? "PARK BRK SET" : "PARK BRK OFF") : null);
            var power = data.ExternalPowerOn ? "EXT PWR" : data.ApuRunning ? "APU" : data.AircraftPowered ? "GEN" : "UNPOWERED";
            page.Node("power", data.AircraftPowered || data.ExternalPowerOn ? DiagState.Normal : DiagState.NotApplicable, power,
                data.ApuRunning ? "APU ON" : "APU OFF");
        }

        var doors = _doors.Read();
        if (doors.All(d => !d.Known))
        {
            page.NoData("doors");
        }
        else
        {
            var open = doors.Where(d => d.Open).ToList();
            var firstPax = open.FirstOrDefault(d => !d.IsCargo);
            var cargoOpen = open.Any(d => d.IsCargo);
            page.Node("doors", DiagState.Normal,
                firstPax is null ? (cargoOpen ? "CARGO OPEN" : "ALL CLSD") : $"{firstPax.Label} OPEN",
                cargoOpen ? "CARGO OPEN" : "CARGO CLSD");
        }

        // Rows
        var ofp = _ofp?.Current;
        if (ofp is not null)
        {
            var runway = _takeoff?.Snapshot()?.Runway ?? ofp.PlannedRunwayOut;
            page.Readout("fpln", $"{ofp.OriginIcao} → {ofp.DestinationIcao}" + (runway is { Length: > 0 } ? $" RWY {runway}" : ""));
            page.Readout("fpln_src", ofp.Source.ToString().ToUpperInvariant());
        }

        var load = _loadsheet?.Snapshot();
        var slot = load?.Final.Status == LoadsheetSlotStatus.Sent ? load.Final : load?.Prelim.Status == LoadsheetSlotStatus.Sent ? load.Prelim : null;
        if (slot is not null)
        {
            page.Readout("load", $"PAX {slot.Pax} ZFW {slot.ZfwKg / 1000:0.0}T BLOCK {slot.FuelKg / 1000:0.0}T");
        }
        else if (ofp is not null)
        {
            page.Readout("load", $"PAX {ofp.PaxCount} ZFW {ofp.EstZfwKg / 1000.0:0.0}T BLOCK {ofp.FuelPlanRampKg / 1000.0:0.0}T");
        }

        var wx = _weather?.Snapshot();
        var airport = wx?.Departure ?? wx?.Arrival;
        if (wx?.FetchedAtUtc is { } fetched && airport is not null)
        {
            var age = page.Events.Now - fetched;
            var wind = airport.WindDirection is { } dir && airport.WindSpeed is { } spd ? $"WIND {dir:000}/{spd}" : "WIND - -";
            page.Readout("wx", $"{airport.Airport} AGE {Math.Max(0, (int)age.TotalMinutes)}MIN {wind}");
        }

        foreach (var (id, _, to) in _edges.Observe([new("PROSIM", nodeState)]))
        {
            page.Events.Push($"{id} {(to == DiagState.Normal ? "CONN" : to == DiagState.NotApplicable ? "OFF" : "LOST")}", to);
        }
    }

    /// <summary>The bus bar: green while fed, amber while reconnecting or when everything is
    /// stale, not applicable with no feed configured.</summary>
    public static DiagState BusState(ConnectionState feed, int stale, int count) => feed switch
    {
        ConnectionState.Connected when count > 0 && stale == count => DiagState.Caution,
        ConnectionState.Connected => DiagState.Normal,
        ConnectionState.Disabled => DiagState.NotApplicable,
        _ => DiagState.Caution,
    };

    private static string Short(string text) => text.Length <= 24 ? text.ToUpperInvariant() : text[..24].ToUpperInvariant();
}
