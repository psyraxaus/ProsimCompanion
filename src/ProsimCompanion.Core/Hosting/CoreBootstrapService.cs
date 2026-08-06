using Microsoft.Extensions.Hosting;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.Profiles;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Hosting;

/// <summary>
/// Starts the core infrastructure (flight state engine) and mirrors the interesting moments —
/// phase transitions, subsystem connection changes — into the session event log.
/// </summary>
public sealed class CoreBootstrapService : IHostedService
{
    private readonly FlightStateEngine _flightState;
    private readonly ConnectionStatusStore _status;
    private readonly JsonlEventLog _eventLog;
    private readonly AircraftProfileService _profiles;

    public CoreBootstrapService(
        FlightStateEngine flightState,
        ConnectionStatusStore status,
        JsonlEventLog eventLog,
        AircraftProfileService profiles,
        // Injected purely to activate their event wiring at startup (deice card arms from a
        // GSX edge that can fire before any web page has resolved it).
        Deice.DeiceHoldoverService deiceHoldover)
    {
        ArgumentNullException.ThrowIfNull(flightState);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(deiceHoldover);

        _flightState = flightState;
        _status = status;
        _eventLog = eventLog;
        _profiles = profiles;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _eventLog.Record("session-started");
        _flightState.PhaseChanged += OnPhaseChanged;
        _status.Changed += OnStatusChanged;
        _profiles.Changed += OnProfileChanged;
        _flightState.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _flightState.PhaseChanged -= OnPhaseChanged;
        _status.Changed -= OnStatusChanged;
        _profiles.Changed -= OnProfileChanged;
        _eventLog.Record("session-ended");
        return Task.CompletedTask;
    }

    private void OnPhaseChanged(object? sender, FlightPhaseChangedEventArgs e)
        => _eventLog.Record("phase-changed", new { previous = e.Previous.ToString(), current = e.Current.ToString() });

    private void OnProfileChanged(object? sender, EventArgs e)
        => _eventLog.Record("profile-changed", new
        {
            profile = _profiles.ActiveProfile?.Name,
            title = _profiles.AircraftTitle,
        });

    private void OnStatusChanged(object? sender, EventArgs e)
        => _eventLog.Record(
            "subsystem-status",
            _status.Snapshot().ToDictionary(pair => pair.Key, pair => pair.Value.ToString()));
}
