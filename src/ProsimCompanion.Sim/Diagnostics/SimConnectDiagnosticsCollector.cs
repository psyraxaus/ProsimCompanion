using System.Globalization;
using ProsimCompanion.Core.Diagnostics.Ecam;
using ProsimCompanion.Core.Diagnostics.Metrics;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Sim.Diagnostics;

/// <summary>
/// The SIM page's SimConnect half: the SIMCONNECT source box (sim name, session phase), the
/// simvar bus figures and the reconnect caution. The feed's health is the footer dot; the
/// session phase is detail only, because "not in a flight" is a fact, not a fault.
/// </summary>
public sealed class SimConnectDiagnosticsCollector : IDiagnosticsCollector
{
    private const string System = "SIM";

    private readonly ConnectionStatusStore _connections;
    private readonly SimSessionStore _session;
    private readonly SimVarService _simVars;
    private readonly RateRegistry _rates;
    private readonly StateTransitions<string> _edges = new();

    public SimConnectDiagnosticsCollector(
        ConnectionStatusStore connections,
        SimSessionStore session,
        SimVarService simVars,
        RateRegistry rates)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(simVars);
        ArgumentNullException.ThrowIfNull(rates);
        _connections = connections;
        _session = session;
        _simVars = simVars;
        _rates = rates;
    }

    public string Page => DiagPageIds.Sim;

    public void Contribute(DiagPageBuilder page, DiagCautionSink cautions)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(cautions);

        var state = _connections.Snapshot().FirstOrDefault(p => p.Key == Subsystems.SimConnect).Value;
        var session = _session.Snapshot();

        var (nodeState, value) = state switch
        {
            ConnectionState.Connected => (DiagState.Normal, EcamTokens.Conn),
            ConnectionState.Connecting => (DiagState.Caution, EcamTokens.Reconn),
            ConnectionState.Disconnected => (DiagState.Caution, EcamTokens.Reconn),
            _ => (DiagState.NotApplicable, EcamTokens.NotApplicable),
        };
        var detail = session.SimVersion?.ToUpperInvariant() ?? (state == ConnectionState.Disabled ? "NO SIMCONNECT" : null);
        page.Node("simconnect", nodeState, value, detail);
        page.Readout("sim_name", session.SimVersion?.ToUpperInvariant());
        page.Readout("sim_session", session.Phase switch
        {
            SimSessionPhase.InSession => session.Paused ? "IN SESSION · PAUSED" : "IN SESSION",
            SimSessionPhase.Walkaround => "WALKAROUND",
            SimSessionPhase.NotInSession => "MAIN MENU",
            _ => null,
        });

        var rate = _rates[RateNames.Simvars].PerSecond;
        page.Readout("rate_simvars", state == ConnectionState.Connected ? rate.ToString("0", CultureInfo.InvariantCulture) : null);
        page.Readout("subs_simvars", _simVars.Count.ToString(CultureInfo.InvariantCulture));
        page.Readout("stale_simvars", _simVars.Stale.ToString(CultureInfo.InvariantCulture));
        page.Node("simvar_bus", BusState(state, _simVars.Stale, _simVars.Count), "SIMVAR BUS");
        page.Link("simconnect", "simvar_bus");

        if (state is ConnectionState.Disconnected or ConnectionState.Connecting)
        {
            cautions.Raise(System, "SIMCONNECT LOST", DiagState.Caution,
                new DiagAction("RECONNECT", EcamTokens.Auto),
                new DiagAction("SIMVARS", EcamTokens.HoldLast));
            cautions.Degraded("SIMVAR FEED");
            page.CautionLine("SIMCONNECT LOST · RECONNECT AUTO");
        }

        foreach (var (id, _, to) in _edges.Observe([new("SIMCONNECT", nodeState)]))
        {
            page.Events.Push($"{id} {(to == DiagState.Normal ? "CONN" : to == DiagState.NotApplicable ? "OFF" : "LOST")}", to);
        }
    }

    /// <summary>Same rule as the dataref bus: green while fed, amber while reconnecting or
    /// when every value is stale, not applicable without a feed.</summary>
    public static DiagState BusState(ConnectionState feed, int stale, int count) => feed switch
    {
        ConnectionState.Connected when count > 0 && stale == count => DiagState.Caution,
        ConnectionState.Connected => DiagState.Normal,
        ConnectionState.Disabled => DiagState.NotApplicable,
        _ => DiagState.Caution,
    };
}
