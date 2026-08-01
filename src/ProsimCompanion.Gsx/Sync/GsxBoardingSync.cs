using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;
using ProsimCompanion.Gsx.Services;

namespace ProsimCompanion.Gsx.Sync;

/// <summary>
/// Progressive boarding sync, the predecessors' seat-map model: the planned map comes from
/// <c>efb.passengers.booked.string</c>; as GSX's boarded counter
/// (<c>L:FSDT_GSX_NUMPASSENGERS_BOARDING_TOTAL</c> — live count; <c>NUMPASSENGERS</c> is the
/// planned total, smoke-test verified) rises, planned seats fill in order and the occupation
/// string is written back — ProSim derives zone loads and CG from it, so partial loads stay
/// CG-realistic by construction. Cargo follows GSX's percentage against the planned cargo
/// weight. Falls back to capacity-proportional zone amounts when no booked seat map exists.
/// EFB boarding status is kept in step ("inProg"/"completed"). Deboarding remains observe-only.
/// </summary>
public sealed class GsxBoardingSync : IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    private readonly GsxProsimWriter _writer;
    private readonly IOptionsMonitor<GsxOptions> _options;
    private readonly GsxDiagnosticsStore _diagnostics;
    private readonly ILogger<GsxBoardingSync> _logger;
    private readonly IDataRefSubscription _plannedTotalLvar;
    private readonly IDataRefSubscription _boardedLvar;
    private readonly IDataRefSubscription _cargoPercent;
    private readonly IDataRefSubscription _deboardTotal;
    private readonly IDataRefSubscription _deboardCargoPercent;
    private readonly IDataRefSubscription _bookedSeatString;
    private readonly IDataRefSubscription[] _zoneCapacities;
    private readonly IDataRefSubscription _plannedCargo;
    private readonly IDataRefSubscription _cargoFwdCapacity;
    private readonly IDataRefSubscription _cargoAftCapacity;
    private readonly Timer _timer;
    private volatile bool _boardingActive;
    private volatile bool _deboardingActive;
    private bool[] _plannedMap = [];
    private bool[] _boardedMap = [];
    private bool _seatMapMode;
    private int _lastWrittenBoarded = -1;
    private double _lastWrittenCargoPct = -1;
    private int _lastLoggedDeboard = -1;
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

        // Counter semantics per the 2026-08-02 live session: NUMPASSENGERS holds the planned
        // total from the start; BOARDING_TOTAL counts up progressively as pax walk aboard.
        _plannedTotalLvar = simVars.Subscribe(GsxLvarNames.NumPassengers, "number", DataRefTier.Normal);
        _boardedLvar = simVars.Subscribe(GsxLvarNames.NumPassengersBoardingTotal, "number", DataRefTier.Normal);
        _cargoPercent = simVars.Subscribe(GsxLvarNames.BoardingCargoPercent, "number", DataRefTier.Normal);
        _deboardTotal = simVars.Subscribe(GsxLvarNames.NumPassengersDeboardingTotal, "number", DataRefTier.Normal);
        _deboardCargoPercent = simVars.Subscribe(GsxLvarNames.DeboardingCargoPercent, "number", DataRefTier.Normal);

        _bookedSeatString = prosim.Subscribe(ProsimDataRefNames.PaxBookedString, DataRefTier.Infrequent);
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
        _timer = new Timer(_ => _ = TickAsync(), null, TickInterval, TickInterval);
    }

    public void Dispose()
    {
        _timer.Dispose();
        _plannedTotalLvar.Dispose();
        _boardedLvar.Dispose();
        _cargoPercent.Dispose();
        _deboardTotal.Dispose();
        _deboardCargoPercent.Dispose();
        _bookedSeatString.Dispose();
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
                    StartBoarding();
                    break;

                case GsxServiceLifecycleEvent.Completed when _boardingActive:
                    _boardingActive = false;
                    _ = FinalizeBoardingAsync();
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

    private void StartBoarding()
    {
        _boardingActive = true;
        _lastWrittenBoarded = -1;
        _lastWrittenCargoPct = -1;

        // Seat-map mode: fill the booked seats progressively so people actually appear in
        // their seats (the predecessors' model). Zone fallback only without a booked map.
        _plannedMap = GsxSeatMap.Parse(_bookedSeatString.GetValue<string?>(null));
        _boardedMap = new bool[_plannedMap.Length];
        _seatMapMode = _plannedMap.Any(seat => seat);

        RecordDecision(
            "boarding sync",
            _seatMapMode
                ? $"activated — seat-map mode, {_plannedMap.Count(s => s)} booked seats, GSX planned {_plannedTotalLvar.GetValue(0.0):F0}"
                : $"activated — no booked seat map, zone-fallback mode, GSX planned {_plannedTotalLvar.GetValue(0.0):F0}");
        _ = _writer.WriteAsync(ProsimDataRefNames.EfbBoardingStatus, "inProg");
    }

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
                var boarded = (int)_boardedLvar.GetValue(0.0);
                if (boarded != _lastWrittenBoarded && boarded >= 0)
                {
                    await WriteBoardedAsync(boarded).ConfigureAwait(false);
                }

                var cargoPct = _cargoPercent.GetValue(0.0);
                if (Math.Abs(cargoPct - _lastWrittenCargoPct) >= 1)
                {
                    await WriteCargoAsync(cargoPct).ConfigureAwait(false);
                }
            }

            if (_deboardingActive)
            {
                // Observe mode: log the counters so live sessions teach us their shape.
                var deboarded = (int)_deboardTotal.GetValue(0.0);
                if (deboarded != _lastLoggedDeboard)
                {
                    _lastLoggedDeboard = deboarded;
                    RecordDecision(
                        "deboarding observe",
                        $"NUMPASSENGERS={_plannedTotalLvar.GetValue(0.0):F0}, DEBOARD_TOTAL={deboarded}, CARGO%={_deboardCargoPercent.GetValue(0.0):F0}");
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

    private async Task FinalizeBoardingAsync()
    {
        var planned = (int)_plannedTotalLvar.GetValue(0.0);
        await WriteBoardedAsync(Math.Max(planned, _lastWrittenBoarded)).ConfigureAwait(false);
        await WriteCargoAsync(100).ConfigureAwait(false);
        await _writer.WriteAsync(ProsimDataRefNames.EfbBoardingStatus, "completed").ConfigureAwait(false);
        RecordDecision("boarding sync", $"completed — reconciled at {planned} pax, cargo 100%");
    }

    private async Task WriteBoardedAsync(int boardedCount)
    {
        if (_seatMapMode)
        {
            var seated = GsxSeatMap.FillBoarded(_plannedMap, _boardedMap, boardedCount);
            if (seated > 0 || boardedCount != _lastWrittenBoarded)
            {
                var ok = await _writer.WriteAsync(
                    ProsimDataRefNames.PaxSeatOccupationString,
                    GsxSeatMap.Build(_boardedMap)).ConfigureAwait(false);
                if (ok)
                {
                    _lastWrittenBoarded = boardedCount;
                }
                _logger.LogDebug(
                    "Boarding: {Boarded} aboard, +{Seated} seated this update (written {Ok})",
                    boardedCount,
                    seated,
                    ok);
            }
            return;
        }

        // Zone fallback: capacity-proportional distribution (equal load factor front-to-back).
        var capacities = _zoneCapacities.Select(zone => zone.GetValue(0)).ToArray();
        if (capacities.Sum() <= 0)
        {
            RecordDecision("boarding sync", "zone capacities not available yet — holding pax writes");
            return;
        }

        var distribution = GsxSyncMath.DistributePax(boardedCount, capacities);
        var zonesOk = await _writer.WriteAsync(ProsimDataRefNames.PaxZone1Amount, distribution[0]).ConfigureAwait(false)
            & await _writer.WriteAsync(ProsimDataRefNames.PaxZone2Amount, distribution[1]).ConfigureAwait(false)
            & await _writer.WriteAsync(ProsimDataRefNames.PaxZone3Amount, distribution[2]).ConfigureAwait(false)
            & await _writer.WriteAsync(ProsimDataRefNames.PaxZone4Amount, distribution[3]).ConfigureAwait(false);
        if (zonesOk)
        {
            _lastWrittenBoarded = boardedCount;
        }
        _logger.LogDebug(
            "Boarding (zone fallback): {Pax} pax -> [{Z1}, {Z2}, {Z3}, {Z4}] (written {Ok})",
            boardedCount, distribution[0], distribution[1], distribution[2], distribution[3], zonesOk);
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
