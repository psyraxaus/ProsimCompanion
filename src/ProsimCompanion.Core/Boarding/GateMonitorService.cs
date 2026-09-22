using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Boarding;

/// <summary>
/// Feeds <see cref="GateMonitorCore"/> from the live stores and publishes the result to
/// <see cref="GateStatusStore"/> (owner spec 2026-09-22). Evidence: the latched GSX Boarding
/// stage and pax counters (<see cref="GsxDiagnosticsStore"/>), ProSim door 1L, the phase
/// engine, and the effective STD. A 1 s tick keeps the STD countdown moving; the store only
/// notifies when the snapshot actually changed. Every state edge is logged and written to the
/// session event log as <c>gate-status-changed</c> for the flight-verification probe.
/// Degrade-not-fail: with GSX or ProSim absent the strip reads CLOSED / "GSX offline".
/// </summary>
public sealed class GateMonitorService : IDisposable
{
    private readonly GsxDiagnosticsStore _diagnostics;
    private readonly ConnectionStatusStore _connections;
    private readonly OfpStore _ofp;
    private readonly LoadsheetStore _loadsheet;
    private readonly IFlightPhaseSource _flight;
    private readonly ISimClock _clock;
    private readonly IOptionsMonitor<FlightStatusOptions> _options;
    private readonly GateStatusStore _store;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<GateMonitorService> _logger;
    private readonly GroundOpsSignals _signals;
    private readonly IDataRefSubscription<bool> _door1L;
    private readonly IDisposable _diagnosticsSubscription;
    private readonly GateMonitorCore _core = new();
    private readonly object _gate = new();
    private readonly Timer _timer;
    private GateState _lastLogged = GateState.Closed;

    public GateMonitorService(
        GsxDiagnosticsStore diagnostics,
        ConnectionStatusStore connections,
        OfpStore ofp,
        LoadsheetStore loadsheet,
        GroundOpsSignals signals,
        IFlightPhaseSource flight,
        IProsimDataRefs dataRefs,
        ISimClock clock,
        IOptionsMonitor<FlightStatusOptions> options,
        GateStatusStore store,
        JsonlEventLog eventLog,
        ILogger<GateMonitorService> logger)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(ofp);
        ArgumentNullException.ThrowIfNull(loadsheet);
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(dataRefs);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _diagnostics = diagnostics;
        _connections = connections;
        _ofp = ofp;
        _loadsheet = loadsheet;
        _signals = signals;
        _flight = flight;
        _clock = clock;
        _options = options;
        _store = store;
        _eventLog = eventLog;
        _logger = logger;

        _door1L = dataRefs.Subscribe(ProsimDataRefNames.Door1L);
        _door1L.ValueChanged += OnEvidenceChanged;
        _diagnosticsSubscription = diagnostics.Observe(_ => Evaluate());
        _flight.PhaseChanged += OnPhaseChanged;
        _signals.FlightCycleReset += OnFlightCycleReset;
        // The countdown and the STD trigger are time-driven; the tick is cheap (one record
        // compare) and the store swallows unchanged results.
        _timer = new Timer(_ => Evaluate(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1));
        // First read now, so a page opened before the first tick sees the real verdict
        // ("GSX offline") rather than the store's empty default.
        Evaluate();
    }

    private void OnEvidenceChanged(object? sender, EventArgs e) => Evaluate();

    private void OnPhaseChanged(object? sender, FlightPhaseChangedEventArgs e) => Evaluate();

    private void OnFlightCycleReset()
    {
        lock (_gate)
        {
            _core.Reset(_clock.UtcNowOrReal);
        }

        Evaluate();
    }

    private void Evaluate()
    {
        try
        {
            GateStatusSnapshot snapshot;
            lock (_gate)
            {
                snapshot = _core.Evaluate(
                    ReadInputs(),
                    GateIdFrom(_diagnostics.Snapshot().GateRequest),
                    _options.CurrentValue.GateFinalCallPaxPercent,
                    _options.CurrentValue.GateFinalCallMinutesBeforeStd);

                if (snapshot.State != _lastLogged)
                {
                    _logger.LogInformation(
                        "Gate monitor: {State} (was {Previous}) — {Detail}, pax {Boarded}/{Target}, STD in {MinutesToStd} min",
                        snapshot.State, _lastLogged, snapshot.Detail,
                        snapshot.PaxBoarded, snapshot.PaxTarget, snapshot.MinutesToStd);
                    _eventLog.Record("gate-status-changed", new
                    {
                        state = snapshot.State.ToString(),
                        previous = _lastLogged.ToString(),
                        detail = snapshot.Detail,
                        paxBoarded = snapshot.PaxBoarded,
                        paxTarget = snapshot.PaxTarget,
                        minutesToStd = snapshot.MinutesToStd,
                        gate = snapshot.GateId,
                    });
                    _lastLogged = snapshot.State;
                }
            }

            _store.Update(_ => snapshot);
        }
        catch (Exception ex)
        {
            // A monitor fault must never reach the timer thread or the store's observers.
            _logger.LogWarning(ex, "Gate monitor evaluation failed");
        }
    }

    private GateMonitorInputs ReadInputs()
    {
        var gsx = _diagnostics.Snapshot();
        var boarding = Service(gsx, GsxServiceIds.Boarding);
        var bridge = Service(gsx, GsxServiceIds.OperateJetways)?.Stage is GsxServiceStage.Active or GsxServiceStage.Completed
            || Service(gsx, GsxServiceIds.OperateStairs)?.Stage is GsxServiceStage.Active or GsxServiceStage.Completed;
        var gsxConnected = _connections.Snapshot()
            .Any(pair => pair.Key == Subsystems.Gsx && pair.Value == ConnectionState.Connected);
        var now = _clock.UtcNowOrReal;

        return new GateMonitorInputs(
            boarding?.Stage,
            gsx.BoardingCounters?.PaxTarget,
            gsx.BoardingCounters?.PaxBoarded,
            _door1L.RawValue is null ? null : _door1L.Value,
            bridge,
            _flight.CurrentPhase,
            ScheduledDeparture.Effective(_loadsheet.Snapshot().StdOverrideUtc, _ofp.Current?.ScheduledOutUtc, now),
            now,
            gsxConnected);
    }

    private static GsxServiceView? Service(GsxDiagnosticsSnapshot gsx, string id)
        => gsx.Services.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>"B12: Confirmed — confirmed as B12" → "B12". Only a confirmed request names
    /// the gate the aircraft is actually at; an armed-but-pending one is not shown.</summary>
    public static string? GateIdFrom(string? gateRequest)
    {
        if (string.IsNullOrWhiteSpace(gateRequest))
        {
            return null;
        }

        var colon = gateRequest.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0)
        {
            return null;
        }

        var status = gateRequest[(colon + 1)..];
        return status.Contains("Confirmed", StringComparison.OrdinalIgnoreCase)
            ? gateRequest[..colon].Trim()
            : null;
    }

    public void Dispose()
    {
        _timer.Dispose();
        _signals.FlightCycleReset -= OnFlightCycleReset;
        _flight.PhaseChanged -= OnPhaseChanged;
        _diagnosticsSubscription.Dispose();
        _door1L.ValueChanged -= OnEvidenceChanged;
        _door1L.Dispose();
    }
}
