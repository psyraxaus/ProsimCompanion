using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;
using ProsimCompanion.Gsx.Services;

namespace ProsimCompanion.Gsx.Sync;

/// <summary>
/// Boarding sync: mirrors GSX's live boarding counters into ProSim. Passengers are distributed
/// across the four cabin zones <b>capacity-proportionally</b> (largest-remainder), so a partial
/// load (e.g. 99 of 132) fills every zone to the same load factor instead of front-filling —
/// keeping the CG where a real load plan would put it. Cargo follows GSX's boarding cargo
/// percentage against the planned cargo weight, split forward/aft by hold capacity (bulk folds
/// into aft — not settable in ProSim).
///
/// Deboarding runs in <b>observe mode</b> for the first flights: counter values are decision-
/// logged but nothing is written, until the live semantics of the deboard counters are
/// confirmed from a real session.
/// </summary>
public sealed class GsxBoardingSync : IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    private readonly GsxProsimWriter _writer;
    private readonly IOptionsMonitor<GsxOptions> _options;
    private readonly GsxDiagnosticsStore _diagnostics;
    private readonly ILogger<GsxBoardingSync> _logger;
    private readonly IDataRefSubscription _numPax;
    private readonly IDataRefSubscription _boardingTotal;
    private readonly IDataRefSubscription _cargoPercent;
    private readonly IDataRefSubscription _deboardTotal;
    private readonly IDataRefSubscription _deboardCargoPercent;
    private readonly IDataRefSubscription[] _zoneCapacities;
    private readonly IDataRefSubscription _plannedCargo;
    private readonly IDataRefSubscription _cargoFwdCapacity;
    private readonly IDataRefSubscription _cargoAftCapacity;
    private readonly Timer _timer;
    private volatile bool _boardingActive;
    private volatile bool _deboardingActive;
    private int _lastWrittenPax = -1;
    private double _lastWrittenCargoPct = -1;
    private int _lastLoggedDeboardPax = -1;
    private int _ticking;

    public GsxBoardingSync(
        GsxServiceLifecycleTracker lifecycle,
        IProsimDataRefs prosim,
        ISimVars simVars,
        GsxProsimWriter writer,
        IOptionsMonitor<GsxOptions> options,
        GsxDiagnosticsStore diagnostics,
        ILogger<GsxBoardingSync> logger)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(prosim);
        ArgumentNullException.ThrowIfNull(simVars);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(logger);

        _writer = writer;
        _options = options;
        _diagnostics = diagnostics;
        _logger = logger;

        _numPax = simVars.Subscribe(GsxLvarNames.NumPassengers, "number", DataRefTier.Normal);
        _boardingTotal = simVars.Subscribe(GsxLvarNames.NumPassengersBoardingTotal, "number", DataRefTier.Normal);
        _cargoPercent = simVars.Subscribe(GsxLvarNames.BoardingCargoPercent, "number", DataRefTier.Normal);
        _deboardTotal = simVars.Subscribe(GsxLvarNames.NumPassengersDeboardingTotal, "number", DataRefTier.Normal);
        _deboardCargoPercent = simVars.Subscribe(GsxLvarNames.DeboardingCargoPercent, "number", DataRefTier.Normal);

        _zoneCapacities =
        [
            prosim.Subscribe(ProsimDataRefNames.PaxZone1Capacity, DataRefTier.Infrequent),
            prosim.Subscribe(ProsimDataRefNames.PaxZone2Capacity, DataRefTier.Infrequent),
            prosim.Subscribe(ProsimDataRefNames.PaxZone3Capacity, DataRefTier.Infrequent),
            prosim.Subscribe(ProsimDataRefNames.PaxZone4Capacity, DataRefTier.Infrequent),
        ];
        _plannedCargo = prosim.Subscribe(ProsimDataRefNames.EfbPlannedCargoKg, DataRefTier.Infrequent);
        _cargoFwdCapacity = prosim.Subscribe(ProsimDataRefNames.CargoForwardCapacity, DataRefTier.Infrequent);
        _cargoAftCapacity = prosim.Subscribe(ProsimDataRefNames.CargoAftCapacity, DataRefTier.Infrequent);

        lifecycle.ServiceEvent += OnServiceEvent;
        _timer = new Timer(_ => Tick(), null, TickInterval, TickInterval);
    }

    public void Dispose()
    {
        _timer.Dispose();
        _numPax.Dispose();
        _boardingTotal.Dispose();
        _cargoPercent.Dispose();
        _deboardTotal.Dispose();
        _deboardCargoPercent.Dispose();
        foreach (var zone in _zoneCapacities)
        {
            zone.Dispose();
        }
        _plannedCargo.Dispose();
        _cargoFwdCapacity.Dispose();
        _cargoAftCapacity.Dispose();
    }

    private bool Enabled => _options.CurrentValue.AutomationEnabled && _options.CurrentValue.BoardingSyncEnabled;

    private void OnServiceEvent(string serviceId, GsxServiceLifecycleEvent lifecycleEvent)
    {
        if (serviceId.Equals("Boarding", StringComparison.OrdinalIgnoreCase))
        {
            switch (lifecycleEvent)
            {
                case GsxServiceLifecycleEvent.Active when Enabled:
                    _boardingActive = true;
                    _lastWrittenPax = -1;
                    _lastWrittenCargoPct = -1;
                    RecordDecision("boarding sync", $"activated — GSX total {_boardingTotal.GetValue(0.0):F0} pax");
                    break;

                case GsxServiceLifecycleEvent.Completed when _boardingActive:
                    _boardingActive = false;
                    // Final reconciliation: everyone aboard, all cargo loaded.
                    var total = (int)_boardingTotal.GetValue(0.0);
                    _ = FinalizeBoardingAsync(total);
                    break;
            }
        }
        else if (serviceId.Equals("Deboarding", StringComparison.OrdinalIgnoreCase))
        {
            _deboardingActive = lifecycleEvent switch
            {
                GsxServiceLifecycleEvent.Active => true,
                GsxServiceLifecycleEvent.Completed => false,
                _ => _deboardingActive,
            };
            if (lifecycleEvent == GsxServiceLifecycleEvent.Active)
            {
                RecordDecision("deboarding", "observe mode — counters logged, no writes until live semantics confirmed");
            }
        }
    }

    private void Tick() => _ = TickAsync();

    private async Task TickAsync()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1)
        {
            return;
        }

        try
        {
            if (_boardingActive && Enabled)
            {
                var pax = (int)_numPax.GetValue(0.0);
                if (pax != _lastWrittenPax && pax >= 0)
                {
                    await WritePaxZonesAsync(pax).ConfigureAwait(false);
                }

                var cargoPct = _cargoPercent.GetValue(0.0);
                if (Math.Abs(cargoPct - _lastWrittenCargoPct) >= 1)
                {
                    await WriteCargoAsync(cargoPct).ConfigureAwait(false);
                }
            }

            if (_deboardingActive)
            {
                // Observe mode: log the counters so the first live session teaches us their shape.
                var deboarded = (int)_numPax.GetValue(0.0);
                if (deboarded != _lastLoggedDeboardPax)
                {
                    _lastLoggedDeboardPax = deboarded;
                    RecordDecision(
                        "deboarding observe",
                        $"NUMPASSENGERS={deboarded}, DEBOARD_TOTAL={_deboardTotal.GetValue(0.0):F0}, CARGO%={_deboardCargoPercent.GetValue(0.0):F0}");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Boarding sync tick failed");
        }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }

    private async Task FinalizeBoardingAsync(int total)
    {
        if (total > 0)
        {
            await WritePaxZonesAsync(total).ConfigureAwait(false);
        }
        await WriteCargoAsync(100).ConfigureAwait(false);
        RecordDecision("boarding sync", $"completed — reconciled at {total} pax, cargo 100%");
    }

    private async Task WritePaxZonesAsync(int totalPax)
    {
        var capacities = _zoneCapacities.Select(zone => zone.GetValue(0)).ToArray();
        if (capacities.Sum() <= 0)
        {
            RecordDecision("boarding sync", "zone capacities not available yet — holding pax writes");
            return;
        }

        // Capacity-proportional: every zone fills to the same load factor, keeping the CG
        // representative for partial loads instead of front-filling. Writes go via the gateway
        // (predecessor-proven for pax datarefs) and every outcome is logged.
        var distribution = GsxSyncMath.DistributePax(totalPax, capacities);
        var ok = await _writer.WriteAsync(ProsimDataRefNames.PaxZone1Amount, distribution[0]).ConfigureAwait(false)
            & await _writer.WriteAsync(ProsimDataRefNames.PaxZone2Amount, distribution[1]).ConfigureAwait(false)
            & await _writer.WriteAsync(ProsimDataRefNames.PaxZone3Amount, distribution[2]).ConfigureAwait(false)
            & await _writer.WriteAsync(ProsimDataRefNames.PaxZone4Amount, distribution[3]).ConfigureAwait(false);
        if (ok)
        {
            _lastWrittenPax = totalPax;
        }
        _logger.LogDebug(
            "Boarding: {Pax} pax -> zones [{Z1}, {Z2}, {Z3}, {Z4}] (written {Ok})",
            totalPax,
            distribution[0],
            distribution[1],
            distribution[2],
            distribution[3],
            ok);
    }

    private async Task WriteCargoAsync(double percent)
    {
        var planned = _plannedCargo.GetValue(0.0);
        if (planned <= 0)
        {
            return;
        }

        var loaded = planned * Math.Clamp(percent, 0, 100) / 100.0;
        var (forward, aft) = GsxSyncMath.SplitCargo(
            loaded,
            _cargoFwdCapacity.GetValue(0.0),
            _cargoAftCapacity.GetValue(0.0));
        var ok = await _writer.WriteAsync(ProsimDataRefNames.CargoForwardAmount, forward).ConfigureAwait(false)
            & await _writer.WriteAsync(ProsimDataRefNames.CargoAftAmount, aft).ConfigureAwait(false);
        if (ok)
        {
            _lastWrittenCargoPct = percent;
        }
        _logger.LogDebug("Boarding cargo {Percent}% -> fwd {Fwd:F0} kg, aft {Aft:F0} kg (written {Ok})", percent, forward, aft, ok);
    }

    private void RecordDecision(string action, string reason)
    {
        _logger.LogInformation("GSX {Action}: {Reason}", action, reason);
        _diagnostics.RecordDecision(new GsxDecisionView(DateTimeOffset.UtcNow, action, reason));
    }
}
