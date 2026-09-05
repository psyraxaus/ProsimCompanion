using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.Profiles;
using ProsimCompanion.Core.State;
using ProsimCompanion.Gsx.Automation;
using ProsimCompanion.Gsx.Protocol;
using ProsimCompanion.Gsx.Services;

namespace ProsimCompanion.Gsx.Sync;

/// <summary>
/// Arrival-side automation, the predecessor's model:
///
/// - <b>Stable-parked detection</b>: on ground, engines off, park brake set, beacon off and
///   stationary, held for a configurable number of seconds — nothing arrival-related fires
///   during a rolling shutdown or a brakes-off tow.
/// - Once stably parked: the FOB is saved per aircraft title (restored at the next session's
///   preparation), GSX's pax counter is armed with the currently-boarded count so deboarding
///   counts the right heads, and the Deboarding service is called when configured.
/// - <b>FOB restore</b>: at preparation, before any flight plan exists, the fuel quantity is
///   set to the value saved for this aircraft (or the configured default) — the aircraft
///   starts the session with the fuel it landed with.
/// </summary>
public sealed class GsxArrivalService : IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
    private const string DeboardingServiceId = "Deboarding";

    private readonly IGsxRemoteApi _api;
    private readonly IGsxTriggerSlot _slot;
    private readonly GsxServiceLifecycleTracker _lifecycle;
    private readonly GsxAutomationService _automation;
    private readonly IFlightPhaseSource _flightState;
    private readonly IProsimDataRefs _prosim;
    private readonly ISimVars _simVars;
    private readonly JsonSettingsFile _settings;
    private readonly AircraftProfileService _profiles;
    private readonly IOptionsMonitor<GsxOptions> _options;
    private readonly GsxDiagnosticsStore _diagnostics;
    private readonly ILogger<GsxArrivalService> _logger;
    private readonly IDataRefSubscription<int> _beacon;
    private readonly IDataRefSubscription<double> _fuelTotal;
    private readonly IDataRefSubscription<string?> _seatOccupation;
    private readonly IDataRefSubscription<bool> _ofpImported;
    private readonly GsxGroundEquipmentService _groundEquipment;
    private readonly GsxJetwayStairsService _jetwayStairs;
    private readonly Timer _timer;
    private ArrivalCore.ArrivalState _arrivalState = ArrivalCore.ArrivalState.Initial;
    private bool _deboardCalled;
    private bool _fobRestored;
    private bool _fobRestoreRefusalLogged;
    private int _ticking;

    public GsxArrivalService(
        IGsxRemoteApi api,
        IGsxTriggerSlot slot,
        GsxServiceLifecycleTracker lifecycle,
        GsxAutomationService automation,
        IFlightPhaseSource flightState,
        IProsimDataRefs prosim,
        ISimVars simVars,
        JsonSettingsFile settings,
        AircraftProfileService profiles,
        GsxGroundEquipmentService groundEquipment,
        GsxJetwayStairsService jetwayStairs,
        IOptionsMonitor<GsxOptions> options,
        GsxDiagnosticsStore diagnostics,
        ILogger<GsxArrivalService> logger)
    {
        ArgumentNullException.ThrowIfNull(groundEquipment);
        ArgumentNullException.ThrowIfNull(jetwayStairs);
        _groundEquipment = groundEquipment;
        _jetwayStairs = jetwayStairs;
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(automation);
        ArgumentNullException.ThrowIfNull(flightState);
        ArgumentNullException.ThrowIfNull(prosim);
        ArgumentNullException.ThrowIfNull(simVars);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(logger);

        _api = api;
        _slot = slot;
        _lifecycle = lifecycle;
        _automation = automation;
        _flightState = flightState;
        _prosim = prosim;
        _simVars = simVars;
        _settings = settings;
        _profiles = profiles;
        _options = options;
        _diagnostics = diagnostics;
        _logger = logger;

        _beacon = prosim.Subscribe(ProsimDataRefNames.OhExtLtBeacon);
        _fuelTotal = prosim.Subscribe(ProsimDataRefNames.FuelTotal);
        _seatOccupation = prosim.Subscribe(ProsimDataRefNames.PaxSeatOccupationString);
        _ofpImported = prosim.Subscribe(ProsimDataRefNames.EfbSimbriefPlanImported);

        _timer = new Timer(_ => Tick(), null, TickInterval, TickInterval);
    }

    public void Dispose()
    {
        _timer.Dispose();
        _beacon.Dispose();
        _fuelTotal.Dispose();
        _seatOccupation.Dispose();
        _ofpImported.Dispose();
    }

    private bool Enabled => _options.CurrentValue.AutomationEnabled;

    private void Tick()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1)
        {
            return;
        }

        try
        {
            // All tick policy is the pure core (campaign #78); this shell gathers inputs and
            // performs the outcome's effects.
            var snapshot = _flightState.Snapshot().Data;
            var options = _options.CurrentValue;
            var outcome = ArrivalCore.Tick(
                _arrivalState,
                new ArrivalCore.ArrivalInputs(
                    Enabled: options.AutomationEnabled,
                    Phase: _automation.Phase,
                    SnapshotValid: snapshot?.IsValid == true,
                    OnGround: snapshot?.OnGround == true,
                    AnyEngineRunning: snapshot?.AnyEngineRunning == true,
                    ParkBrakeSet: snapshot?.ParkBrakeSet == true,
                    GroundSpeedKt: snapshot?.GroundSpeedKt ?? double.MaxValue,
                    BeaconOn: _beacon.Value != 0,
                    ArrivalStableSecondsOption: options.ArrivalStableSeconds,
                    AutoCallDeboard: options.AutoCallDeboardOnArrival,
                    DeboardAlreadyCalled: _deboardCalled,
                    AutoGroundEquipment: options.AutoGroundEquipment,
                    ChockDelayMinSec: options.ChockDelayMinSec,
                    ChockDelayMaxSec: options.ChockDelayMaxSec),
                Random.Shared.Next);
            _arrivalState = outcome.State;

            if (outcome.ResetDeboardLatch)
            {
                _deboardCalled = false;
            }
            if (outcome.RunArrivalActions)
            {
                RecordDecision("arrival", $"stable parked for {outcome.State.StableSeconds}s — running arrival actions");
                SaveFob();
                ArmDeboardPaxTarget();
            }
            if (outcome.RunJetwayArrivalStep)
            {
                _ = _jetwayStairs.RunArrivalStep();
            }
            if (outcome.TryCallDeboard)
            {
                TryCallDeboarding();
            }
            if (outcome.ChockCountdownStarted is { } chockDelay)
            {
                RecordDecision("arrival", $"placing chocks in {chockDelay}s");
            }
            if (outcome.ChockAborted)
            {
                RecordDecision("arrival", "chock placement aborted — parked state no longer stable");
            }
            if (outcome.PlaceChocks)
            {
                _ = _groundEquipment.PlaceArrivalEquipmentAsync();
            }
            if (outcome.TryRestoreFob)
            {
                TryRestoreFob();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Arrival service tick failed");
        }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }

    /// <summary>FOB persisted per aircraft title into settings.json (gsx.fuelFobSaved) — the
    /// options monitor reloads it, so the next preparation restore sees it immediately.</summary>
    private void SaveFob()
    {
        if (!_options.CurrentValue.FuelSaveLoadFob)
        {
            return;
        }

        var title = _profiles.AircraftTitle;
        if (string.IsNullOrWhiteSpace(title))
        {
            RecordDecision("arrival", "FOB not saved — aircraft title unknown");
            return;
        }

        var fuel = Math.Round(_fuelTotal.Value, 1);
        if (fuel <= 0)
        {
            return;
        }

        _settings.Update(root =>
        {
            var gsx = JsonSettingsFile.GetOrCreateSection(root, GsxOptions.SectionName);
            var saved = JsonSettingsFile.GetOrCreateSection(gsx, "fuelFobSaved");
            saved[title] = fuel;
        });
        RecordDecision("arrival", $"saved FOB {fuel:F0} kg for '{title}'");
    }

    /// <summary>GSX's pax counter is re-armed with the boarded count so deboarding counts the
    /// same heads that boarded (predecessor: SetPaxTarget(PaxBoarded) at arrival).</summary>
    private void ArmDeboardPaxTarget()
    {
        var boarded = SeatMap.Parse(_seatOccupation.Value).Count(seat => seat);
        if (boarded <= 0)
        {
            return;
        }

        try
        {
            _ = _simVars.WriteAsync(GsxLvarNames.NumPassengers.Name, boarded);
            RecordDecision("arrival", $"armed GSX deboard pax target: {boarded}");
        }
        catch (InvalidOperationException ex)
        {
            RecordDecision("arrival", $"deboard pax target write failed: {ex.Message}");
        }
    }

    private void TryCallDeboarding()
    {
        if (_api.Readiness != GsxReadiness.Ready)
        {
            return;
        }

        var deboarding = _api.Mirror.Services.GetValueOrDefault(DeboardingServiceId);
        if (deboarding is not { State: GsxServiceState.Callable, CanTrigger: true })
        {
            return;
        }

        _deboardCalled = true;
        RecordDecision("arrival", "calling Deboarding");
        _ = TriggerDeboardingAsync();
    }

    /// <summary>Dispatches through the shared trigger slot. The cycle is marked called by the
    /// slot on GSX's confirming edge, not up front; anything short of Dispatched re-arms the
    /// 1 Hz tick so the call is re-offered — including a silent drop, which the old direct
    /// send never retried.</summary>
    private async Task TriggerDeboardingAsync()
    {
        var dispatch = await _slot
            .TryDispatchAsync(new GsxTriggerRequest(DeboardingServiceId, "arrival")
            {
                RetryOnce = true,
                // Even the automatic retry can drop — un-latch so the tick keeps offering.
                OnResolved = resolution =>
                {
                    if (resolution != GsxTriggerResolution.Confirmed)
                    {
                        _deboardCalled = false;
                    }
                },
            })
            .ConfigureAwait(false);
        if (dispatch.Status != GsxTriggerDispatchStatus.Dispatched)
        {
            _deboardCalled = false; // retry on a later tick
            RecordDecision(
                "arrival",
                dispatch.Status == GsxTriggerDispatchStatus.Busy
                    ? $"Deboarding call waiting — trigger slot busy with {dispatch.BusyServiceId ?? "another service"}"
                    : $"Deboarding trigger rejected ({dispatch.RejectCode})");
        }
    }

    /// <summary>Why the preparation-time FOB restore did or did not run — the decision is a
    /// pure function (<see cref="DecideFobRestore"/>) so the write-safety rule is testable
    /// without the timer or GSX plumbing.</summary>
    internal enum FobRestoreDecision
    {
        Restore,

        /// <summary>Startup restore (issue #124): a SAVED landing-fuel value applied at the
        /// start of a fresh session — never the configured default.</summary>
        RestoreAtStartup,
        AlreadyRestored,
        Disabled,
        PlanImported,
        NotSafeAtStartup,
    }

    /// <summary>Write-safety gate for the FOB restore (issue #59, flight test 2026-08-16): a
    /// bogus startup phase classification walked the automation straight into Preparation and
    /// the restore overwrote 9576 kg of freshly-loaded fuel with 3344 kg saved by a PREVIOUS
    /// session. After an arrival in THIS session (verifiably airborne) the restore runs
    /// unconditionally, default fallback included. At STARTUP (issue #124, owner report
    /// 2026-09-05: the saved landing fuel was never applied and the session started on
    /// ProSim's own 9576 kg) it runs only under the narrow gate that could not exist in the
    /// #59 era: the flight-live gate holds (session live, datarefs fresh and plausible —
    /// #114), the phase is a real at-the-stand phase, a value was actually SAVED for this
    /// aircraft, and no flight plan has been imported. The configured reset default is
    /// deliberately excluded at startup — it would clobber deliberately-loaded fuel.</summary>
    internal static FobRestoreDecision DecideFobRestore(
        bool alreadyRestored,
        bool saveLoadFobEnabled,
        bool planImported,
        bool airborneThisSession,
        bool flightLive = false,
        FlightPhase phase = FlightPhase.Unknown,
        bool savedValueExists = false)
    {
        if (alreadyRestored)
        {
            return FobRestoreDecision.AlreadyRestored;
        }

        if (!saveLoadFobEnabled)
        {
            return FobRestoreDecision.Disabled;
        }

        if (planImported)
        {
            return FobRestoreDecision.PlanImported;
        }

        if (airborneThisSession)
        {
            return FobRestoreDecision.Restore;
        }

        return savedValueExists
            && flightLive
            && phase is FlightPhase.ColdAndDark or FlightPhase.Preflight
            ? FobRestoreDecision.RestoreAtStartup
            : FobRestoreDecision.NotSafeAtStartup;
    }

    /// <summary>Restores the saved FOB at preparation, before any plan is loaded — mirrors the
    /// predecessor's guard (restore only when no flight plan exists yet, so a mid-turnaround
    /// restart never clobbers planned fuel) plus the gates of <see cref="DecideFobRestore"/>:
    /// turnaround restores run freely (issue #59's airborne proof), a startup restore applies
    /// only an actually-saved value under the flight-live gate (issue #124).</summary>
    private void TryRestoreFob()
    {
        var flight = _flightState.Snapshot();
        var savedTitle = _profiles.AircraftTitle;
        var decision = DecideFobRestore(
            _fobRestored,
            _options.CurrentValue.FuelSaveLoadFob,
            _ofpImported.Value,
            flight.HasBeenAirborneThisSession,
            _flightState.IsLive,
            flight.Phase,
            savedValueExists: !string.IsNullOrWhiteSpace(savedTitle)
                && _options.CurrentValue.FuelFobSaved.ContainsKey(savedTitle));
        if (decision == FobRestoreDecision.NotSafeAtStartup)
        {
            if (!_fobRestoreRefusalLogged)
            {
                _fobRestoreRefusalLogged = true;
                RecordDecision(
                    "fob restore",
                    "held at startup — waiting for a saved value with the flight live at the stand "
                    + "(a blind restore here would overwrite loaded fuel, issue #59)");
            }

            return;
        }

        if (decision is not (FobRestoreDecision.Restore or FobRestoreDecision.RestoreAtStartup))
        {
            return;
        }

        var title = _profiles.AircraftTitle;
        if (string.IsNullOrWhiteSpace(title))
        {
            return; // title arrives with the sim — retry next tick
        }

        // −1 sentinel (GetValueOr, #83): "not read yet" must stay distinguishable from a real
        // 0 kg — the catalog fallback is 0.0, which would let a restore race the real value.
        var current = _fuelTotal.GetValueOr(-1.0);
        if (current < 0)
        {
            return; // ProSim fuel not read yet — a restore now would race the real value
        }

        _fobRestored = true;
        var options = _options.CurrentValue;
        var saved = options.FuelFobSaved.TryGetValue(title, out var value) ? value : options.FuelResetDefaultKg;
        _ = RestoreFobAsync(title, current, saved, decision == FobRestoreDecision.RestoreAtStartup);
    }

    private async Task RestoreFobAsync(string title, double current, double target, bool atStartup)
    {
        try
        {
            await _prosim.WriteAsync(ProsimDataRefNames.FuelTotal, target).ConfigureAwait(false);
            RecordDecision("fob restore", atStartup
                ? $"'{title}': {current:F0} kg -> {target:F0} kg (saved landing fuel applied at session start, issue #124)"
                : $"'{title}': {current:F0} kg -> {target:F0} kg (saved value{(Math.Abs(target - _options.CurrentValue.FuelResetDefaultKg) < 0.1 ? " or default" : "")})");
        }
        catch (InvalidOperationException ex)
        {
            _fobRestored = false; // ProSim write path not ready — retry
            _logger.LogDebug("FOB restore deferred: {Reason}", ex.Message);
        }
    }

    private void RecordDecision(string action, string reason)
    {
        _logger.LogInformation("GSX {Action}: {Reason}", action, reason);
        _diagnostics.RecordDecision(new GsxDecisionView(DateTimeOffset.UtcNow, action, reason));
    }
}
