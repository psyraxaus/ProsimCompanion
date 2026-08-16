using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;
using ProsimCompanion.Gsx.Automation;
using ProsimCompanion.Gsx.Services;

namespace ProsimCompanion.Gsx.Sync;

/// <summary>
/// Fuel transfer sync: while the GSX Refueling service is active AND the physical hose is
/// connected (LVAR — "the API drives the answer; LVARs drive the timing"), ProSim's fuel
/// quantity steps toward the refuel target each second at the configured rate; disconnecting
/// the hose pauses the transfer. Target: <c>aircraft.refuel.fuelTarget.kg</c>, falling back to
/// <c>efb.plannedfuel</c>. Every start/pause/resume/completion is decision-logged.
/// </summary>
public sealed class GsxRefuelSync : IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
    private const double CompletionToleranceKg = 1.0;

    /// <summary>Predecessor FuelCompareVariance: FOB within this of the plan counts as "already
    /// fueled" for the tankering skip.</summary>
    private const double TankeringToleranceKg = 25.0;

    private readonly IProsimDataRefs _prosim;
    private readonly GsxProsimWriter _writer;
    private readonly IOptionsMonitor<GsxOptions> _options;
    private readonly GsxDiagnosticsStore _diagnostics;
    private readonly IGsxFlightPlanStatus _flightPlan;
    private readonly ILogger<GsxRefuelSync> _logger;
    private readonly IDataRefSubscription _fuelTotal;
    private readonly IDataRefSubscription _fuelTarget;
    private readonly IDataRefSubscription _fuelTargetKg;
    private readonly IDataRefSubscription _plannedFuel;
    private readonly IDataRefSubscription _hoseConnected;
    private readonly Timer _timer;
    private volatile bool _transferActive;
    private volatile bool _pendingPlanArm;
    private bool _hoseWasConnected;
    private bool _pumpPowerOn;
    private string? _lastHoldReason;
    private double _latchedTargetKg;
    private double _dynamicRateKgPerSec;
    private bool _divergenceLogged;
    private int _ticking;

    public GsxRefuelSync(
        GsxServiceLifecycleTracker lifecycle,
        IProsimDataRefs prosim,
        ISimVars simVars,
        GsxProsimWriter writer,
        IGsxFlightPlanStatus flightPlan,
        IOptionsMonitor<GsxOptions> options,
        GsxDiagnosticsStore diagnostics,
        ILogger<GsxRefuelSync> logger)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(prosim);
        ArgumentNullException.ThrowIfNull(simVars);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(flightPlan);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(logger);

        _prosim = prosim;
        _writer = writer;
        _flightPlan = flightPlan;
        _options = options;
        _diagnostics = diagnostics;
        _logger = logger;

        _fuelTotal = prosim.Subscribe(ProsimDataRefNames.FuelTotal, DataRefTier.Normal);
        _fuelTarget = prosim.Subscribe(ProsimDataRefNames.RefuelFuelTarget, DataRefTier.Infrequent);
        _fuelTargetKg = prosim.Subscribe(ProsimDataRefNames.RefuelFuelTargetKg, DataRefTier.Infrequent);
        _plannedFuel = prosim.Subscribe(ProsimDataRefNames.EfbPlannedFuel, DataRefTier.Infrequent);
        _hoseConnected = simVars.Subscribe(GsxLvarNames.FuelHoseConnected, "number", DataRefTier.Normal);

        lifecycle.ServiceEvent += OnServiceEvent;
        _timer = new Timer(_ => Tick(), null, TickInterval, TickInterval);
    }

    public void Dispose()
    {
        _timer.Dispose();
        _fuelTotal.Dispose();
        _fuelTarget.Dispose();
        _fuelTargetKg.Dispose();
        _plannedFuel.Dispose();
        _hoseConnected.Dispose();
    }

    private void OnServiceEvent(string serviceId, GsxServiceLifecycleEvent lifecycleEvent)
    {
        if (!serviceId.Equals("Refueling", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        switch (lifecycleEvent)
        {
            case GsxServiceLifecycleEvent.Active when Enabled:
                // Plan gate on ARMING (issue #60, 2026-08-16 flight evidence): requests coming
                // from the GSX side (EFB, menu, gsx-handler refuelingRequested) bypass every
                // app-side gate, and the sync used to latch a target from stale/absent plan
                // data — that flight's refuel ran 55 s before the SimBrief plan arrived. With
                // requireOfpBeforeDeparture the sync now refuses to latch until a plan exists;
                // the tick arms it the moment one arrives (the same flight latched 5300 kg
                // fine once the plan was imported — that recovery is preserved).
                if (_options.CurrentValue.RequireOfpBeforeDeparture && !_flightPlan.FlightPlanAvailable)
                {
                    _pendingPlanArm = true;
                    RecordDecision(
                        "refuel sync",
                        "GSX is refueling without a flight plan — sync holds until one arrives "
                        + "(gsx.requireOfpBeforeDeparture); no fuel target latched");
                    break;
                }

                ActivateTransfer();
                break;

            case GsxServiceLifecycleEvent.Completed when _pendingPlanArm:
                _pendingPlanArm = false;
                RecordDecision("refuel sync", "GSX refuel completed while holding for a flight plan — no fuel was moved");
                break;

            case GsxServiceLifecycleEvent.Completed when _transferActive:
                _transferActive = false;
                _ = FinishOnGsxCompleteAsync();
                break;
        }
    }

    /// <summary>Arms the transfer for the current GSX refuel cycle. The target is LATCHED once
    /// at activation and never re-read while pumping: ProSim rewrites
    /// <c>aircraft.refuel.fuelTarget</c> to the current FOB the moment its refuel session
    /// engages (round-4 smoke test: 7317 collapsed to 2500 one tick after pump-on, ending the
    /// transfer immediately). Power stays OFF until the hose actually connects and a sane
    /// transfer is possible.</summary>
    private void ActivateTransfer()
    {
        _transferActive = true;
        _hoseWasConnected = false;
        _lastHoldReason = null;
        _divergenceLogged = false;
        _pumpPowerOn = false;
        _dynamicRateKgPerSec = 0;
        _latchedTargetKg = ReadTargetKg();
        RecordDecision(
            "refuel sync",
            $"activated — current {_fuelTotal.GetValue(0.0):F0} kg; latched target {_latchedTargetKg:F0} kg (candidates: fuelTarget {_fuelTarget.GetValue(0.0):F0}, fuelTarget.kg {_fuelTargetKg.GetValue(0.0):F0}, plannedfuel {_plannedFuel.GetValue(0.0):F0})");
        _ = TrySkipForTankering(_fuelTotal.GetValue(0.0), _latchedTargetKg);
    }

    private bool Enabled => _options.CurrentValue.AutomationEnabled && _options.CurrentValue.RefuelSyncEnabled;

    /// <summary>Transfer progress (0–100, current FOB against the latched target) while a
    /// transfer is active; null otherwise. Cached reads only — added for the status API.</summary>
    public double? ProgressPercent
    {
        get
        {
            if (!_transferActive)
            {
                return null;
            }

            var target = _latchedTargetKg;
            return target <= 0
                ? 0.0
                : Math.Clamp(_fuelTotal.GetValue(0.0) / target * 100.0, 0.0, 100.0);
        }
    }

    /// <summary>The intended TOTAL: aircraft.refuel.fuelTarget (the EFB fuel page's figure),
    /// falling back to efb.plannedfuel, rounded UP to the next 100 kg (real-world fuel-order
    /// increments — also covers targets typed into the EFB by hand). The .kg variant has
    /// proven to be a transfer amount and is logged for diagnosis only. Used only to latch —
    /// never trusted mid-transfer.</summary>
    private double ReadTargetKg()
    {
        var target = _fuelTarget.GetValue(0.0);
        return LoadMath.RoundFuelUpToHundredKg(target > 0 ? target : _plannedFuel.GetValue(0.0));
    }

    private void Tick() => _ = TickAsync();

    private async Task TickAsync()
    {
        if ((!_transferActive && !_pendingPlanArm) || !Enabled || Interlocked.Exchange(ref _ticking, 1) == 1)
        {
            return;
        }

        try
        {
            // Deferred arming (issue #60): a plan-less GSX refuel parked the sync; arm the
            // moment the flight plan appears so the mid-service import recovery still works.
            if (_pendingPlanArm)
            {
                if (!_flightPlan.FlightPlanAvailable)
                {
                    return;
                }

                _pendingPlanArm = false;
                RecordDecision("refuel sync", "flight plan arrived mid-service — arming now");
                ActivateTransfer();
                if (!_transferActive)
                {
                    return; // tankering skip inside the activation
                }
            }

            var hose = _hoseConnected.GetValue(0.0) != 0;
            if (hose != _hoseWasConnected)
            {
                _hoseWasConnected = hose;

                // Hose pulled mid-transfer: optionally finish instantly at the latched target
                // (predecessor RefuelFinishOnHose) instead of pausing until it reconnects.
                if (!hose && _options.CurrentValue.RefuelFinishOnHose && _latchedTargetKg > 0)
                {
                    RecordDecision("refuel sync", $"hose disconnected — finishing instantly at {_latchedTargetKg:F0} kg (refuelFinishOnHose)");
                    _transferActive = false;
                    _pumpPowerOn = false;
                    await _prosim.WriteAsync(ProsimDataRefNames.FuelTotal, _latchedTargetKg).ConfigureAwait(false);
                    await SetRefuelPowerAsync(false).ConfigureAwait(false);
                    return;
                }

                RecordDecision("refuel sync", hose ? "hose connected" : "hose disconnected — paused");
            }

            if (!hose)
            {
                await SetPumpAsync(false).ConfigureAwait(false);
                return;
            }

            var current = _fuelTotal.GetValue(0.0);
            if (_latchedTargetKg <= 0)
            {
                // Not latched at activation (target not yet written) — keep trying until a
                // real figure appears, but only before any pumping has started.
                _latchedTargetKg = ReadTargetKg();
                if (_latchedTargetKg <= 0)
                {
                    HoldOnce("no fuel target available — waiting");
                    return;
                }
                RecordDecision("refuel sync", $"latched target {_latchedTargetKg:F0} kg");
                if (TrySkipForTankering(current, _latchedTargetKg))
                {
                    return;
                }
            }

            var target = _latchedTargetKg;
            var liveTarget = ReadTargetKg();
            if (!_divergenceLogged && Math.Abs(liveTarget - target) > CompletionToleranceKg)
            {
                _divergenceLogged = true;
                RecordDecision(
                    "refuel sync",
                    $"live fuelTarget changed to {liveTarget:F0} kg mid-transfer — keeping latched {target:F0} kg (ProSim rewrites the target while refueling)");
            }

            // Defuel guard: fuel-target datarefs have twice exposed transfer amounts instead of
            // totals. A target below current holds (never pumps down) unless explicitly allowed.
            if (target < current - CompletionToleranceKg && !_options.CurrentValue.AllowDefuel)
            {
                HoldOnce($"target {target:F0} kg is below current {current:F0} kg — defuel disabled (gsx.allowDefuel)");
                await SetPumpAsync(false).ConfigureAwait(false);
                return;
            }

            var next = LoadMath.NextFuelStep(current, target, GetRateKgPerSec(current, target));
            if (Math.Abs(next - current) < 0.01)
            {
                await SetPumpAsync(false).ConfigureAwait(false);
                return;
            }

            // Fuel is actually about to move: refuel power reflects real pumping only.
            await SetPumpAsync(true).ConfigureAwait(false);

            try
            {
                // Fuel quantity goes via the SDK (verified live) — awaited, never fire-and-forget.
                await _prosim.WriteAsync(ProsimDataRefNames.FuelTotal, next).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                RecordDecision("refuel sync", $"fuel write failed: {ex.Message}");
                return;
            }

            if (Math.Abs(next - target) <= CompletionToleranceKg)
            {
                _transferActive = false;
                _pumpPowerOn = false;
                RecordDecision("refuel sync", $"target reached at {target:F0} kg");
                await SetRefuelPowerAsync(false).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Refuel sync tick failed");
        }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }

    /// <summary>Tankering skip (predecessor SkipFuelOnTankering): with the FOB already at or
    /// above the plan (within tolerance) the whole transfer is skipped — the GSX crew still
    /// runs its animation, but no fuel moves and refuel power never comes on.</summary>
    private bool TrySkipForTankering(double currentKg, double targetKg)
    {
        if (!_options.CurrentValue.SkipRefuelOnTankering
            || targetKg <= 0
            || currentKg < targetKg - TankeringToleranceKg)
        {
            return false;
        }

        _transferActive = false;
        RecordDecision(
            "refuel sync",
            $"skipped — FOB {currentKg:F0} kg already meets planned {targetKg:F0} kg (tankering)");
        return true;
    }

    /// <summary>Per-tick rate. Dynamic method computes it once per transfer from the FIRST
    /// tick's remaining amount over the time target, so the fill takes ~the configured duration
    /// regardless of the ordered quantity; a nonsensical result falls back to the fixed rate.</summary>
    private double GetRateKgPerSec(double currentKg, double targetKg)
    {
        var options = _options.CurrentValue;
        if (!string.Equals(options.RefuelMethod, "dynamicRate", StringComparison.OrdinalIgnoreCase))
        {
            return options.RefuelRateKgPerSec;
        }

        if (_dynamicRateKgPerSec <= 0)
        {
            var seconds = Math.Max(1, options.RefuelTimeTargetSeconds);
            _dynamicRateKgPerSec = (targetKg - currentKg) / seconds;
            if (_dynamicRateKgPerSec <= 0)
            {
                _dynamicRateKgPerSec = options.RefuelRateKgPerSec;
            }
            RecordDecision(
                "refuel sync",
                $"dynamic rate {_dynamicRateKgPerSec:F1} kg/s ({targetKg - currentKg:F0} kg over ~{seconds} s)");
        }

        return _dynamicRateKgPerSec;
    }

    /// <summary>GSX finished the refuel cycle. If the transfer fell short of the latched target
    /// (hose pulled early, pauses), snap the FOB to the target — the predecessor's proven
    /// reconciliation — then drop refuel power.</summary>
    private async Task FinishOnGsxCompleteAsync()
    {
        try
        {
            var current = _fuelTotal.GetValue(0.0);
            var target = _latchedTargetKg;
            if (target > 0 && current < target - CompletionToleranceKg)
            {
                await _prosim.WriteAsync(ProsimDataRefNames.FuelTotal, target).ConfigureAwait(false);
                RecordDecision("refuel sync", $"GSX reports refuel complete at {current:F0} kg — snapped to latched target {target:F0} kg");
            }
            else
            {
                RecordDecision("refuel sync", $"GSX reports refuel complete at {current:F0} kg");
            }
        }
        catch (InvalidOperationException ex)
        {
            RecordDecision("refuel sync", $"completion snap failed: {ex.Message}");
        }
        finally
        {
            await SetRefuelPowerAsync(false).ConfigureAwait(false);
        }
    }

    /// <summary>Idempotent pump-power switch: only writes on actual transitions, so refuel
    /// power is on exactly while fuel is moving.</summary>
    private async Task SetPumpAsync(bool on)
    {
        if (_pumpPowerOn == on)
        {
            return;
        }
        _pumpPowerOn = on;
        RecordDecision("refuel sync", on ? "pump on — fuel transferring" : "pump off");
        await SetRefuelPowerAsync(on).ConfigureAwait(false);
    }

    private void HoldOnce(string reason)
    {
        if (string.Equals(reason, _lastHoldReason, StringComparison.Ordinal))
        {
            return;
        }
        _lastHoldReason = reason;
        RecordDecision("refuel sync", reason);
    }

    private Task<bool> SetRefuelPowerAsync(bool on)
        // Refuel power is an EFB-domain toggle — gateway path, outcome logged by the writer.
        => _writer.WriteAsync(ProsimDataRefNames.RefuelPower, on);

    private void RecordDecision(string action, string reason)
    {
        _logger.LogInformation("GSX {Action}: {Reason}", action, reason);
        _diagnostics.RecordDecision(new GsxDecisionView(DateTimeOffset.UtcNow, action, reason));
    }
}

