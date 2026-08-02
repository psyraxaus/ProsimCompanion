using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;
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

    private readonly IProsimDataRefs _prosim;
    private readonly GsxProsimWriter _writer;
    private readonly IOptionsMonitor<GsxOptions> _options;
    private readonly GsxDiagnosticsStore _diagnostics;
    private readonly ILogger<GsxRefuelSync> _logger;
    private readonly IDataRefSubscription _fuelTotal;
    private readonly IDataRefSubscription _fuelTarget;
    private readonly IDataRefSubscription _fuelTargetKg;
    private readonly IDataRefSubscription _plannedFuel;
    private readonly IDataRefSubscription _hoseConnected;
    private readonly Timer _timer;
    private volatile bool _transferActive;
    private bool _hoseWasConnected;
    private bool _pumpPowerOn;
    private string? _lastHoldReason;
    private double _latchedTargetKg;
    private bool _divergenceLogged;
    private int _ticking;

    public GsxRefuelSync(
        GsxServiceLifecycleTracker lifecycle,
        IProsimDataRefs prosim,
        ISimVars simVars,
        GsxProsimWriter writer,
        IOptionsMonitor<GsxOptions> options,
        GsxDiagnosticsStore diagnostics,
        ILogger<GsxRefuelSync> logger)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(prosim);
        ArgumentNullException.ThrowIfNull(simVars);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(logger);

        _prosim = prosim;
        _writer = writer;
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
                _transferActive = true;
                _hoseWasConnected = false;
                _lastHoldReason = null;
                _divergenceLogged = false;
                _pumpPowerOn = false;
                // The target is LATCHED once at activation and never re-read while pumping:
                // ProSim rewrites aircraft.refuel.fuelTarget to the current FOB the moment its
                // refuel session engages (round-4 smoke test: 7317 collapsed to 2500 one tick
                // after pump-on, ending the transfer immediately). Power stays OFF until the
                // hose actually connects and a sane transfer is possible.
                _latchedTargetKg = ReadTargetKg();
                RecordDecision(
                    "refuel sync",
                    $"activated — current {_fuelTotal.GetValue(0.0):F0} kg; latched target {_latchedTargetKg:F0} kg (candidates: fuelTarget {_fuelTarget.GetValue(0.0):F0}, fuelTarget.kg {_fuelTargetKg.GetValue(0.0):F0}, plannedfuel {_plannedFuel.GetValue(0.0):F0})");
                break;

            case GsxServiceLifecycleEvent.Completed when _transferActive:
                _transferActive = false;
                _ = FinishOnGsxCompleteAsync();
                break;
        }
    }

    private bool Enabled => _options.CurrentValue.AutomationEnabled && _options.CurrentValue.RefuelSyncEnabled;

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
        if (!_transferActive || !Enabled || Interlocked.Exchange(ref _ticking, 1) == 1)
        {
            return;
        }

        try
        {
            var hose = _hoseConnected.GetValue(0.0) != 0;
            if (hose != _hoseWasConnected)
            {
                _hoseWasConnected = hose;
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

            var next = LoadMath.NextFuelStep(current, target, _options.CurrentValue.RefuelRateKgPerSec);
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

