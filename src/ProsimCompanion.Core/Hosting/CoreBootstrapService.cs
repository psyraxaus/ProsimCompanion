using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Diagnostics;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.Profiles;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Hosting;

/// <summary>
/// Starts the core infrastructure (flight state engine) and mirrors the interesting moments —
/// phase transitions, subsystem connection changes — into the session event log. Also owns the
/// session header: every session file (initial or rotated) opens with the build that wrote it.
/// </summary>
public sealed class CoreBootstrapService : IHostedService
{
    private readonly FlightStateEngine _flightState;
    private readonly ConnectionStatusStore _status;
    private readonly JsonlEventLog _eventLog;
    private readonly AircraftProfileService _profiles;
    private readonly Gate.ArrivalGateCoordinator _arrivalGate;
    private readonly IAppBuildInfo _build;
    private readonly IOptionsMonitor<FlightStateOptions> _flightStateOptions;
    private readonly ILogger<CoreBootstrapService> _logger;

    public CoreBootstrapService(
        FlightStateEngine flightState,
        ConnectionStatusStore status,
        JsonlEventLog eventLog,
        AircraftProfileService profiles,
        // Injected purely to activate their event wiring at startup (deice card arms from a
        // GSX edge that can fire before any web page has resolved it; the arrival-gate
        // coordinator must hear the cruise transition even with no browser open).
        Deice.DeiceHoldoverService deiceHoldover,
        Gate.ArrivalGateCoordinator arrivalGate,
        IAppBuildInfo build,
        IOptionsMonitor<FlightStateOptions> flightStateOptions,
        ILogger<CoreBootstrapService> logger)
    {
        ArgumentNullException.ThrowIfNull(flightState);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(deiceHoldover);
        ArgumentNullException.ThrowIfNull(arrivalGate);
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(flightStateOptions);
        ArgumentNullException.ThrowIfNull(logger);

        _flightState = flightState;
        _status = status;
        _eventLog = eventLog;
        _profiles = profiles;
        _arrivalGate = arrivalGate;
        _build = build;
        _flightStateOptions = flightStateOptions;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _eventLog.Record(SessionHeader.StartedEvent, BuildHeader(_eventLog.Path));
        _eventLog.SessionRotated += OnSessionRotated;
        _flightState.PhaseChanged += OnPhaseChanged;
        _status.Changed += OnStatusChanged;
        _profiles.Changed += OnProfileChanged;
        _flightState.Start();
        RestoreArrivalGate();
        return Task.CompletedTask;
    }

    /// <summary>The persisted arrival gate comes back before any page or phase edge needs it
    /// (2026-09-20: in-flight restarts lost it). A failure here is logged, never fatal.</summary>
    private void RestoreArrivalGate()
    {
        try
        {
            var (action, gate) = _arrivalGate.Restore();
            if (action != Gate.ArrivalGateRestoreAction.Ignore)
            {
                _eventLog.Record("arrival-gate-restored", new
                {
                    gate,
                    action = action.ToString(),
                    phase = _flightState.CurrentPhase.ToString(),
                });
            }
        }
        catch (Exception ex)
        {
            _eventLog.Record("arrival-gate-restore-failed", new { error = ex.Message });
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _eventLog.SessionRotated -= OnSessionRotated;
        _flightState.PhaseChanged -= OnPhaseChanged;
        _status.Changed -= OnStatusChanged;
        _profiles.Changed -= OnProfileChanged;
        _eventLog.Record("session-ended");
        return Task.CompletedTask;
    }

    /// <summary>The version stamp for a session file. Exposed for tests.</summary>
    public SessionHeader BuildHeader(string sessionPath)
        => SessionHeader.Create(
            _build,
            sessionPath,
            _flightStateOptions.CurrentValue.SampleIntervalSeconds,
            _profiles.ActiveProfile?.Name);

    /// <summary>A rotated file (company day mode) gets the same header as its first line, and
    /// the CMTrace log gets the banner again so a daily file that started mid-session still
    /// names the build near the top of the day.</summary>
    private void OnSessionRotated(object? sender, SessionRotatedEventArgs e)
    {
        var header = BuildHeader(e.NewPath);
        _eventLog.Record(SessionHeader.RotatedEvent, header);
        _logger.LogInformation(
            SessionHeader.BannerTemplate,
            header.AppVersion, header.Commit ?? "no commit", header.Os, header.Runtime, header.SessionFile);
    }

    private void OnPhaseChanged(object? sender, FlightPhaseChangedEventArgs e)
    {
        // The snapshot rides along so the post-flight debrief can recover lift-off IAS and
        // touchdown ground speed from the transition record alone (Prosim2FO's phase.changed
        // carried the same data; without it those facts are unrecoverable after the fact).
        var snapshot = _flightState.LastSnapshot;
        _eventLog.Record("phase-changed", new
        {
            previous = e.Previous.ToString(),
            current = e.Current.ToString(),
            // Which rule fired and why (review 2026-08-29): a replay diff and a probe can name
            // the edge; "manual-override" / "session-ended" are the engine's own ids.
            rule = e.RuleId,
            reason = e.Reason,
            // Full phase-relevant evidence, not just speeds (issue #93): a bogus transition
            // must be diagnosable from this record alone — the 2026-08-17 Unknown→Approach
            // recurrence of #59 had to be reconstructed from evaluator code paths.
            snapshot = snapshot is null ? null : new
            {
                iasKt = Math.Round(snapshot.IndicatedAirspeedKt, 1),
                groundSpeedKt = Math.Round(snapshot.GroundSpeedKt, 1),
                onGround = snapshot.OnGround,
                altitudeFt = Math.Round(snapshot.AltitudeFt),
                radioAltitudeFt = Math.Round(snapshot.RadioAltitudeFt),
                verticalSpeedFpm = Math.Round(snapshot.VerticalSpeedFpm),
                powered = snapshot.AircraftPowered,
                enginesRunning = snapshot.AnyEngineRunning,
                engineStarting = snapshot.EngineStarting,
                pushback = snapshot.PushbackActive,
                parkBrake = snapshot.ParkBrakeSet,
                gearDown = snapshot.GearDown,
                takeoffThrust = snapshot.TakeoffThrustSet,
                // #100: the pushback boolean proved untrustworthy — the beacon/APU gate
                // inputs and the raw enum ride along so the dataref's real semantics can be
                // settled from one instrumented flight.
                beacon = snapshot.BeaconOn,
                apuRunning = snapshot.ApuRunning,
                rawPushback = snapshot.RawPushbackState,
                // #105: the cruise-entry altitude gate's input — a Cruise commit must be
                // checkable against the FMS cruise level from this record alone.
                fmsCruiseAltFt = Math.Round(snapshot.FmsCruiseAltFt),
            },
        });
    }

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
