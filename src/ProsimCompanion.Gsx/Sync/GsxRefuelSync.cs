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
    private readonly IOptionsMonitor<GsxOptions> _options;
    private readonly GsxDiagnosticsStore _diagnostics;
    private readonly ILogger<GsxRefuelSync> _logger;
    private readonly IDataRefSubscription _fuelTotal;
    private readonly IDataRefSubscription _fuelTargetKg;
    private readonly IDataRefSubscription _plannedFuel;
    private readonly IDataRefSubscription _hoseConnected;
    private readonly Timer _timer;
    private volatile bool _transferActive;
    private bool _hoseWasConnected;
    private int _ticking;

    public GsxRefuelSync(
        GsxServiceLifecycleTracker lifecycle,
        IProsimDataRefs prosim,
        ISimVars simVars,
        IOptionsMonitor<GsxOptions> options,
        GsxDiagnosticsStore diagnostics,
        ILogger<GsxRefuelSync> logger)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(prosim);
        ArgumentNullException.ThrowIfNull(simVars);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(logger);

        _prosim = prosim;
        _options = options;
        _diagnostics = diagnostics;
        _logger = logger;

        _fuelTotal = prosim.Subscribe(ProsimDataRefNames.FuelTotal, DataRefTier.Normal);
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
                RecordDecision("refuel sync", $"activated — target {TargetKg():F0} kg, current {_fuelTotal.GetValue(0.0):F0} kg");
                _ = SetRefuelPowerAsync(true);
                break;

            case GsxServiceLifecycleEvent.Completed when _transferActive:
                _transferActive = false;
                RecordDecision("refuel sync", $"GSX reports refuel complete at {_fuelTotal.GetValue(0.0):F0} kg");
                _ = SetRefuelPowerAsync(false);
                break;
        }
    }

    private bool Enabled => _options.CurrentValue.AutomationEnabled && _options.CurrentValue.RefuelSyncEnabled;

    private double TargetKg()
    {
        var target = _fuelTargetKg.GetValue(0.0);
        return target > 0 ? target : _plannedFuel.GetValue(0.0);
    }

    private void Tick()
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
                RecordDecision("refuel sync", hose ? "hose connected — transferring" : "hose disconnected — paused");
            }

            if (!hose)
            {
                return;
            }

            var target = TargetKg();
            if (target <= 0)
            {
                RecordDecision("refuel sync", "no fuel target available — waiting");
                return;
            }

            var current = _fuelTotal.GetValue(0.0);
            var next = GsxSyncMath.NextFuelStep(current, target, _options.CurrentValue.RefuelRateKgPerSec);
            if (Math.Abs(next - current) < 0.01)
            {
                return;
            }

            _ = _prosim.WriteAsync(ProsimDataRefNames.FuelTotal, next);

            if (Math.Abs(next - target) <= CompletionToleranceKg)
            {
                _transferActive = false;
                RecordDecision("refuel sync", $"target reached at {target:F0} kg");
                _ = SetRefuelPowerAsync(false);
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

    private async Task SetRefuelPowerAsync(bool on)
    {
        try
        {
            await _prosim.WriteAsync(ProsimDataRefNames.RefuelPower, on).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("Refuel power write skipped: {Message}", ex.Message);
        }
    }

    private void RecordDecision(string action, string reason)
    {
        _logger.LogInformation("GSX {Action}: {Reason}", action, reason);
        _diagnostics.RecordDecision(new GsxDecisionView(DateTimeOffset.UtcNow, action, reason));
    }
}
