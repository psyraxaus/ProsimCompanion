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
    private readonly Lock _stateLock = new();
    private RefuelCore.RefuelState _state = RefuelCore.RefuelState.Idle;
    private string? _lastHoldReason;
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

        // All policy — plan gate on arming (#60), target latching, tankering skip, snap on
        // completion — is the pure core (campaign #78); this handler feeds it and applies.
        switch (lifecycleEvent)
        {
            case GsxServiceLifecycleEvent.Active when Enabled:
                _lastHoldReason = null;
                _ = ApplyAsync(RunCore(state => RefuelCore.OnRefuelActive(state, GatherInputs())));
                break;

            case GsxServiceLifecycleEvent.Completed:
                _ = ApplyAsync(RunCore(state => RefuelCore.OnRefuelCompleted(state, GatherInputs())));
                break;
        }
    }

    private bool Enabled => _options.CurrentValue.AutomationEnabled && _options.CurrentValue.RefuelSyncEnabled;

    /// <summary>Transfer progress (0–100, current FOB against the latched target) while a
    /// transfer is active; null otherwise. Cached reads only — added for the status API.</summary>
    public double? ProgressPercent
    {
        get
        {
            var state = _state;
            if (!state.TransferActive)
            {
                return null;
            }

            var target = state.LatchedTargetKg;
            return target <= 0
                ? 0.0
                : Math.Clamp(_fuelTotal.GetValue(0.0) / target * 100.0, 0.0, 100.0);
        }
    }

    private void Tick() => _ = TickAsync();

    private async Task TickAsync()
    {
        var state = _state;
        if ((!state.TransferActive && !state.PendingPlanArm) || !Enabled
            || Interlocked.Exchange(ref _ticking, 1) == 1)
        {
            return;
        }

        try
        {
            await ApplyAsync(RunCore(s => RefuelCore.Tick(s, GatherInputs()))).ConfigureAwait(false);
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

    /// <summary>Snapshot of everything the core needs — cached dataref reads only.</summary>
    private RefuelCore.RefuelInputs GatherInputs()
    {
        var options = _options.CurrentValue;
        return new RefuelCore.RefuelInputs(
            RequireOfp: options.RequireOfpBeforeDeparture,
            PlanAvailable: _flightPlan.FlightPlanAvailable,
            HoseConnected: _hoseConnected.GetValue(0.0) != 0,
            CurrentKg: _fuelTotal.GetValue(0.0),
            FuelTargetRaw: _fuelTarget.GetValue(0.0),
            FuelTargetKgRaw: _fuelTargetKg.GetValue(0.0),
            PlannedFuelRaw: _plannedFuel.GetValue(0.0),
            FinishOnHose: options.RefuelFinishOnHose,
            AllowDefuel: options.AllowDefuel,
            SkipOnTankering: options.SkipRefuelOnTankering,
            RefuelMethod: options.RefuelMethod,
            FixedRateKgPerSec: options.RefuelRateKgPerSec,
            TimeTargetSeconds: options.RefuelTimeTargetSeconds);
    }

    /// <summary>Runs a core transition under the state lock (events and ticks interleave).</summary>
    private RefuelCore.RefuelOutcome RunCore(Func<RefuelCore.RefuelState, RefuelCore.RefuelOutcome> transition)
    {
        lock (_stateLock)
        {
            var outcome = transition(_state);
            _state = outcome.State;
            return outcome;
        }
    }

    /// <summary>Performs the outcome's effects in the core's intended order: decisions, hold,
    /// power-on (before fuel moves), the awaited fuel write, power-off (after). A failed fuel
    /// write is logged and the rest skipped — the GSX Completed snap reconciles later.</summary>
    private async Task ApplyAsync(RefuelCore.RefuelOutcome outcome)
    {
        foreach (var decision in outcome.Decisions)
        {
            RecordDecision("refuel sync", decision);
        }
        if (outcome.Hold is not null)
        {
            HoldOnce(outcome.Hold);
        }

        if (outcome.RefuelPower == true)
        {
            await SetRefuelPowerAsync(true).ConfigureAwait(false);
        }

        if (outcome.WriteFuelKg is { } fuelKg)
        {
            try
            {
                // Fuel quantity goes via the SDK (verified live) — awaited, never fire-and-forget.
                await _prosim.WriteAsync(ProsimDataRefNames.FuelTotal, fuelKg).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                RecordDecision("refuel sync", $"fuel write failed: {ex.Message}");
            }
        }

        if (outcome.RefuelPower == false)
        {
            await SetRefuelPowerAsync(false).ConfigureAwait(false);
        }
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

