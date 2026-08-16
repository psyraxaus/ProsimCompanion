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
    private readonly GsxServiceLifecycleTracker _lifecycle;
    private readonly GsxAutomationService _automation;
    private readonly FlightStateEngine _flightState;
    private readonly IProsimDataRefs _prosim;
    private readonly ISimVars _simVars;
    private readonly JsonSettingsFile _settings;
    private readonly AircraftProfileService _profiles;
    private readonly IOptionsMonitor<GsxOptions> _options;
    private readonly GsxDiagnosticsStore _diagnostics;
    private readonly ILogger<GsxArrivalService> _logger;
    private readonly IDataRefSubscription _beacon;
    private readonly IDataRefSubscription _fuelTotal;
    private readonly IDataRefSubscription _seatOccupation;
    private readonly IDataRefSubscription _ofpImported;
    private readonly GsxGroundEquipmentService _groundEquipment;
    private readonly GsxJetwayStairsService _jetwayStairs;
    private readonly Timer _timer;
    private int _stableSeconds;
    private bool _arrivalHandled;
    private bool _deboardCalled;
    private bool _fobRestored;
    private bool _fobRestoreRefusalLogged;
    private bool _wasInArrivalPhases;
    private int _chockCountdown = -1;
    private int _ticking;

    public GsxArrivalService(
        IGsxRemoteApi api,
        GsxServiceLifecycleTracker lifecycle,
        GsxAutomationService automation,
        FlightStateEngine flightState,
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

        _beacon = prosim.Subscribe(ProsimDataRefNames.OhExtLtBeacon, DataRefTier.Normal);
        _fuelTotal = prosim.Subscribe(ProsimDataRefNames.FuelTotal, DataRefTier.Normal);
        _seatOccupation = prosim.Subscribe(ProsimDataRefNames.PaxSeatOccupationString, DataRefTier.Infrequent);
        _ofpImported = prosim.Subscribe(ProsimDataRefNames.EfbSimbriefPlanImported, DataRefTier.Infrequent);

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
            if (!Enabled)
            {
                return;
            }

            var phase = _automation.Phase;
            var inArrivalPhases = phase is GsxAutomationPhase.TaxiIn or GsxAutomationPhase.Arrival;

            // A fresh arrival segment resets the arrival latches.
            if (inArrivalPhases && !_wasInArrivalPhases)
            {
                _stableSeconds = 0;
                _arrivalHandled = false;
                _deboardCalled = false;
                _chockCountdown = -1;
            }

            // The phase engine flips Shutdown -> Preflight quickly once parked (turnaround);
            // keep processing the arrival until its actions have actually run.
            var pendingArrival = _wasInArrivalPhases && !_arrivalHandled
                && phase == GsxAutomationPhase.Preparation;
            _wasInArrivalPhases = inArrivalPhases || pendingArrival;

            if (inArrivalPhases || pendingArrival)
            {
                TickArrival();
            }
            else if (phase == GsxAutomationPhase.Preparation)
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

    private void TickArrival()
    {
        var snapshot = _flightState.LastSnapshot;
        var stable = snapshot is { IsValid: true, OnGround: true, AnyEngineRunning: false, ParkBrakeSet: true }
            && snapshot.GroundSpeedKt < 2.0
            && _beacon.GetValue(0) == 0;
        _stableSeconds = stable ? _stableSeconds + 1 : 0;

        if (_arrivalHandled)
        {
            // Jetway/stairs first (predecessor order: gate path before pax leave), then
            // deboarding — both keep retrying until they succeed or give up.
            _ = _jetwayStairs.RunArrivalStep();
            if (!_deboardCalled && _options.CurrentValue.AutoCallDeboardOnArrival)
            {
                TryCallDeboarding();
            }

            TickChockCountdown(stable);
            return;
        }

        if (_stableSeconds < Math.Max(1, _options.CurrentValue.ArrivalStableSeconds))
        {
            return;
        }

        _arrivalHandled = true;
        RecordDecision("arrival", $"stable parked for {_stableSeconds}s — running arrival actions");

        SaveFob();
        ArmDeboardPaxTarget();
        if (_options.CurrentValue.AutoCallDeboardOnArrival)
        {
            TryCallDeboarding();
        }

        // Randomized chock delay (predecessor ChockDelayMin/Max): the ground crew takes a
        // human moment to walk the chocks out after shutdown.
        var options = _options.CurrentValue;
        if (options.AutoGroundEquipment)
        {
            var lo = Math.Max(0, options.ChockDelayMinSec);
            var hi = Math.Max(lo + 1, options.ChockDelayMaxSec);
            _chockCountdown = Random.Shared.Next(lo, hi);
            RecordDecision("arrival", $"placing chocks in {_chockCountdown}s");
        }
    }

    /// <summary>Counts the arrival chock delay down one tick at a time — only while the
    /// aircraft stays stably parked. Movement mid-countdown aborts the placement outright
    /// (predecessor rule: "parked state no longer stable").</summary>
    private void TickChockCountdown(bool stable)
    {
        if (_chockCountdown < 0)
        {
            return;
        }

        if (!stable)
        {
            _chockCountdown = -1;
            RecordDecision("arrival", "chock placement aborted — parked state no longer stable");
            return;
        }

        if (--_chockCountdown <= 0)
        {
            _chockCountdown = -1;
            _ = _groundEquipment.PlaceArrivalEquipmentAsync();
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

        var fuel = Math.Round(_fuelTotal.GetValue(0.0), 1);
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
        var boarded = SeatMap.Parse(_seatOccupation.GetValue<string?>(null)).Count(seat => seat);
        if (boarded <= 0)
        {
            return;
        }

        try
        {
            _ = _simVars.WriteAsync(GsxLvarNames.NumPassengers, boarded);
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
        _lifecycle.MarkCalled(DeboardingServiceId);
        RecordDecision("arrival", "calling Deboarding");
        _ = TriggerDeboardingAsync();
    }

    private async Task TriggerDeboardingAsync()
    {
        var result = await _api.SendCommandAsync(
            "service.trigger",
            new JsonObject { ["service"] = DeboardingServiceId }).ConfigureAwait(false);
        if (!result.Ok)
        {
            _deboardCalled = false; // retry on a later tick
            RecordDecision("arrival", $"Deboarding trigger rejected ({result.Code})");
        }
    }

    /// <summary>Why the preparation-time FOB restore did or did not run — the decision is a
    /// pure function (<see cref="DecideFobRestore"/>) so the write-safety rule is testable
    /// without the timer or GSX plumbing.</summary>
    internal enum FobRestoreDecision
    {
        Restore,
        AlreadyRestored,
        Disabled,
        PlanImported,
        NoArrivalThisSession,
    }

    /// <summary>Write-safety gate for the FOB restore (issue #59, flight test 2026-08-16): a
    /// bogus startup phase classification walked the automation straight into Preparation and
    /// the restore overwrote 9576 kg of freshly-loaded fuel with 3344 kg saved by a PREVIOUS
    /// session. The restore is a turnaround convenience — it may only run after the aircraft
    /// has verifiably been airborne in THIS session, never at startup, where whatever fuel is
    /// already on board is authoritative.</summary>
    internal static FobRestoreDecision DecideFobRestore(
        bool alreadyRestored, bool saveLoadFobEnabled, bool planImported, bool airborneThisSession)
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

        return airborneThisSession ? FobRestoreDecision.Restore : FobRestoreDecision.NoArrivalThisSession;
    }

    /// <summary>Restores the saved FOB at preparation, before any plan is loaded — mirrors the
    /// predecessor's guard (restore only when no flight plan exists yet, so a mid-turnaround
    /// restart never clobbers planned fuel) plus the arrival-this-session gate (issue #59:
    /// never restore at startup — see <see cref="DecideFobRestore"/>).</summary>
    private void TryRestoreFob()
    {
        var decision = DecideFobRestore(
            _fobRestored,
            _options.CurrentValue.FuelSaveLoadFob,
            _ofpImported.GetValue(false),
            _flightState.HasBeenAirborneThisSession);
        if (decision == FobRestoreDecision.NoArrivalThisSession)
        {
            if (!_fobRestoreRefusalLogged)
            {
                _fobRestoreRefusalLogged = true;
                RecordDecision(
                    "fob restore",
                    "refused — aircraft has not been airborne this session (a startup restore would overwrite loaded fuel)");
            }

            return;
        }

        if (decision != FobRestoreDecision.Restore)
        {
            return;
        }

        var title = _profiles.AircraftTitle;
        if (string.IsNullOrWhiteSpace(title))
        {
            return; // title arrives with the sim — retry next tick
        }

        var current = _fuelTotal.GetValue(-1.0);
        if (current < 0)
        {
            return; // ProSim fuel not read yet — a restore now would race the real value
        }

        _fobRestored = true;
        var options = _options.CurrentValue;
        var saved = options.FuelFobSaved.TryGetValue(title, out var value) ? value : options.FuelResetDefaultKg;
        _ = RestoreFobAsync(title, current, saved);
    }

    private async Task RestoreFobAsync(string title, double current, double target)
    {
        try
        {
            await _prosim.WriteAsync(ProsimDataRefNames.FuelTotal, target).ConfigureAwait(false);
            RecordDecision("fob restore", $"'{title}': {current:F0} kg -> {target:F0} kg (saved value{(Math.Abs(target - _options.CurrentValue.FuelResetDefaultKg) < 0.1 ? " or default" : "")})");
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
