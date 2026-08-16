using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;
using ProsimCompanion.Gsx.Automation;
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
/// EFB boarding status is kept in step ("inProg"/"completed").
///
/// Deboarding mirrors in reverse (predecessor semantics: DEBOARDING_TOTAL counts UP as pax
/// leave): seats empty from the FRONT of the cabin, cargo drains by GSX's unload percentage,
/// and completion reconciles to an empty aircraft. The raw counters stay decision-logged so
/// live sessions keep teaching us their shape.
/// </summary>
public sealed class GsxBoardingSync : IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    private readonly GsxProsimWriter _writer;
    private readonly IOptionsMonitor<GsxOptions> _options;
    private readonly GsxDiagnosticsStore _diagnostics;
    private readonly IGsxFlightPlanStatus _flightPlan;
    private readonly ILogger<GsxBoardingSync> _logger;
    private readonly IDataRefSubscription _plannedTotalLvar;
    private readonly IDataRefSubscription _boardedLvar;
    private readonly IDataRefSubscription _cargoPercent;
    private readonly IDataRefSubscription _deboardTotal;
    private readonly IDataRefSubscription _deboardCargoPercent;
    private readonly IDataRefSubscription _bookedSeatString;
    private readonly IDataRefSubscription _seatOccupationString;
    private readonly IDataRefSubscription[] _zoneCapacities;
    private readonly IDataRefSubscription _plannedCargo;
    private readonly IDataRefSubscription _cargoFwdCapacity;
    private readonly IDataRefSubscription _cargoAftCapacity;
    private readonly Timer _timer;
    private volatile bool _boardingActive;
    private volatile bool _pendingPlanArm;
    private volatile bool _deboardingActive;
    private bool[] _plannedMap = [];
    private bool[] _boardedMap = [];
    private bool _seatMapMode;
    private int _lastWrittenBoarded = -1;
    private double _lastWrittenCargoPct = -1;
    private bool[] _deboardMap = [];
    private int _deboardStartCount;
    private int _lastWrittenDeboard = -1;
    private double _lastWrittenDeboardCargoPct = -1;
    private GsxBoardingCountersView? _lastPublishedCounters;
    private int _ticking;

    public GsxBoardingSync(
        GsxServiceLifecycleTracker lifecycle,
        IProsimDataRefs prosim,
        ISimVars simVars,
        GsxProsimWriter writer,
        IGsxFlightPlanStatus flightPlan,
        IOptionsMonitor<GsxOptions> options,
        GsxDiagnosticsStore diagnostics,
        ILogger<GsxBoardingSync> logger)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(prosim);
        ArgumentNullException.ThrowIfNull(simVars);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(flightPlan);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(logger);

        _writer = writer;
        _flightPlan = flightPlan;
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
        _seatOccupationString = prosim.Subscribe(ProsimDataRefNames.PaxSeatOccupationString, DataRefTier.Infrequent);
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
        _seatOccupationString.Dispose();
        foreach (var zone in _zoneCapacities)
        {
            zone.Dispose();
        }
        _plannedCargo.Dispose();
        _cargoFwdCapacity.Dispose();
        _cargoAftCapacity.Dispose();
    }

    private bool Enabled => _options.CurrentValue.AutomationEnabled && _options.CurrentValue.BoardingSyncEnabled;

    /// <summary>Planned pax total from GSX's counter LVAR (armed by the automation with the
    /// booked manifest); null while unarmed. Cached reads only — added for the status API.</summary>
    public int? PaxTotal
    {
        get
        {
            var total = (int)_plannedTotalLvar.GetValue(0.0);
            return total > 0 ? total : null;
        }
    }

    /// <summary>Pax boarded so far per GSX's live boarding counter; null while the planned
    /// total is unknown (the counter alone means nothing).</summary>
    public int? PaxBoarded => PaxTotal is null ? null : (int)_boardedLvar.GetValue(0.0);

    /// <summary>Passengers still aboard while a deboard runs: planned total minus GSX's
    /// cumulative (up-counting by design) DEBOARDING_TOTAL. Null outside a deboarding session —
    /// the boarding counter freezes then, which left the Stream Deck deboard key stuck at
    /// total/total (issue #37).</summary>
    public int? PaxRemaining
    {
        get
        {
            if (!_deboardingActive || PaxTotal is not int total)
            {
                return null;
            }

            return Math.Max(0, total - (int)_deboardTotal.GetValue(0.0));
        }
    }

    private void OnServiceEvent(string serviceId, GsxServiceLifecycleEvent lifecycleEvent)
    {
        if (serviceId.Equals("Boarding", StringComparison.OrdinalIgnoreCase))
        {
            switch (lifecycleEvent)
            {
                case GsxServiceLifecycleEvent.Active when Enabled:
                    // Plan gate on ARMING (issue #60): a GSX-side boarding request with no
                    // flight plan must not latch OFP-derived seat maps/cargo — the sync holds
                    // and arms from the tick once a plan arrives. Deboarding (arrival flow,
                    // nothing OFP-derived to latch) is deliberately never plan-gated.
                    if (_options.CurrentValue.RequireOfpBeforeDeparture && !_flightPlan.FlightPlanAvailable)
                    {
                        _pendingPlanArm = true;
                        RecordDecision(
                            "boarding sync",
                            "GSX is boarding without a flight plan — sync holds until one arrives "
                            + "(gsx.requireOfpBeforeDeparture); no manifest latched");
                        break;
                    }

                    StartBoarding();
                    break;

                case GsxServiceLifecycleEvent.Completed when _pendingPlanArm:
                    _pendingPlanArm = false;
                    RecordDecision("boarding sync", "GSX boarding completed while holding for a flight plan — no pax/cargo were loaded");
                    break;

                case GsxServiceLifecycleEvent.Completed when _boardingActive:
                    _boardingActive = false;
                    _ = FinalizeBoardingAsync();
                    break;
            }
        }
        else if (serviceId.Equals("Deboarding", StringComparison.OrdinalIgnoreCase))
        {
            switch (lifecycleEvent)
            {
                case GsxServiceLifecycleEvent.Active when DeboardEnabled:
                    StartDeboarding();
                    break;

                case GsxServiceLifecycleEvent.Completed when _deboardingActive:
                    _deboardingActive = false;
                    _ = FinalizeDeboardingAsync();
                    break;
            }
        }
    }

    private bool DeboardEnabled => Enabled && _options.CurrentValue.DeboardingSyncEnabled;

    /// <summary>The deboard map starts from the CURRENT occupation (what actually boarded),
    /// and drains from the front as GSX's up-counting DEBOARDING_TOTAL rises.</summary>
    private void StartDeboarding()
    {
        _deboardMap = SeatMap.Parse(_seatOccupationString.GetValue<string?>(null));
        _deboardStartCount = _deboardMap.Count(seat => seat);
        _lastWrittenDeboard = -1;
        _lastWrittenDeboardCargoPct = -1;
        _deboardingActive = true;
        RecordDecision("deboarding sync", $"activated — {_deboardStartCount} pax aboard to deboard");
    }

    private void StartBoarding()
    {
        _boardingActive = true;
        _lastWrittenBoarded = -1;
        _lastWrittenCargoPct = -1;
        _seatMapMode = false;
        _plannedMap = [];
        _boardedMap = [];

        RecordDecision("boarding sync", $"activated — GSX planned {_plannedTotalLvar.GetValue(0.0):F0} pax");
        _ = _writer.WriteAsync(ProsimDataRefNames.EfbBoardingStatus, "inProg");
    }

    /// <summary>
    /// Establishes the planned seat map: the OFP-derived booked string when present, otherwise a
    /// synthesized capacity-proportional map from GSX's planned count (written back to the
    /// booked dataref so ProSim's manifest agrees). Zone-amount writes are gone for good — those
    /// datarefs are read-only (owner-verified CanWrite=False); the seat-occupation string is the
    /// only real loading path, exactly as the predecessor did it.
    /// </summary>
    private async Task<bool> EnsureMapsAsync()
    {
        if (_seatMapMode)
        {
            return true;
        }

        var booked = SeatMap.Parse(_bookedSeatString.GetValue<string?>(null));
        if (booked.Any(seat => seat))
        {
            _plannedMap = booked;
            _boardedMap = new bool[booked.Length];
            _seatMapMode = true;
            RecordDecision("boarding sync", $"seat-map mode — {booked.Count(s => s)} booked seats from the EFB manifest");
            return true;
        }

        var planned = (int)_plannedTotalLvar.GetValue(0.0);
        var capacities = _zoneCapacities.Select(zone => zone.GetValue(0)).ToArray();
        if (planned <= 0 || capacities.Sum() <= 0)
        {
            RecordDecision("boarding sync", "waiting for pax data (no booked map, GSX planned count/zone capacities not ready)");
            return false;
        }

        _plannedMap = SeatMap.SynthesizeBooked(planned, capacities);
        _boardedMap = new bool[_plannedMap.Length];
        _seatMapMode = true;
        RecordDecision("boarding sync", $"seat-map mode — synthesized {planned} booked seats (capacity-proportional, randomized within zones; no OFP manifest)");
        _ = await _writer.WriteAsync(ProsimDataRefNames.PaxBookedString, SeatMap.Build(_plannedMap)).ConfigureAwait(false);
        return true;
    }

    private async Task TickAsync()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1)
        {
            return;
        }

        try
        {
            // Deferred arming (issue #60): boarding went active plan-less and the sync held;
            // the moment the plan arrives, arm normally — the boarded-counter catch-up below
            // then seats everyone GSX has already boarded in one update.
            if (_pendingPlanArm && Enabled && _flightPlan.FlightPlanAvailable)
            {
                _pendingPlanArm = false;
                RecordDecision("boarding sync", "flight plan arrived mid-service — arming now");
                StartBoarding();
            }

            if (_boardingActive && Enabled)
            {
                var boarded = (int)_boardedLvar.GetValue(0.0);
                if (boarded != _lastWrittenBoarded && boarded >= 0 && await EnsureMapsAsync().ConfigureAwait(false))
                {
                    await WriteBoardedAsync(boarded).ConfigureAwait(false);
                }

                var cargoPct = _cargoPercent.GetValue(0.0);
                if (Math.Abs(cargoPct - _lastWrittenCargoPct) >= 1)
                {
                    await WriteCargoAsync(cargoPct).ConfigureAwait(false);
                }
            }

            if (_deboardingActive && DeboardEnabled)
            {
                var deboarded = (int)_deboardTotal.GetValue(0.0);
                if (deboarded != _lastWrittenDeboard && deboarded >= 0 && _deboardMap.Length > 0)
                {
                    // Raw counters stay visible for semantics verification (first live run).
                    _logger.LogDebug(
                        "Deboarding counters: NUMPASSENGERS={Planned}, DEBOARD_TOTAL={Deboarded}, CARGO%={CargoPct}",
                        _plannedTotalLvar.GetValue(0.0),
                        deboarded,
                        _deboardCargoPercent.GetValue(0.0));
                    await WriteDeboardedAsync(deboarded).ConfigureAwait(false);
                }

                var unloadPct = _deboardCargoPercent.GetValue(0.0);
                if (Math.Abs(unloadPct - _lastWrittenDeboardCargoPct) >= 1)
                {
                    _lastWrittenDeboardCargoPct = unloadPct;
                    // DEBOARDING_CARGO_PERCENT counts unload progress up: remaining = 100 - pct.
                    await WriteCargoAsync(100 - Math.Clamp(unloadPct, 0, 100)).ConfigureAwait(false);
                }
            }

            PublishCounters();
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
        if (await EnsureMapsAsync().ConfigureAwait(false))
        {
            await WriteBoardedAsync(Math.Max(planned, _lastWrittenBoarded)).ConfigureAwait(false);
        }
        await WriteCargoAsync(100).ConfigureAwait(false);
        await _writer.WriteAsync(ProsimDataRefNames.EfbBoardingStatus, "completed").ConfigureAwait(false);
        RecordDecision("boarding sync", $"completed — reconciled at {planned} pax, cargo 100%");
    }

    /// <summary>Empties seats front-first until remaining = start − deboarded, writing the
    /// occupation string back so ProSim's zone loads and CG track the deboard.</summary>
    private async Task WriteDeboardedAsync(int deboardedCount)
    {
        var targetRemaining = Math.Max(0, _deboardStartCount - deboardedCount);
        var unseated = SeatMap.DrainBoarded(_deboardMap, targetRemaining);
        if (unseated > 0 || deboardedCount != _lastWrittenDeboard)
        {
            var ok = await _writer.WriteAsync(
                ProsimDataRefNames.PaxSeatOccupationString,
                SeatMap.Build(_deboardMap)).ConfigureAwait(false);
            if (ok)
            {
                _lastWrittenDeboard = deboardedCount;
            }
            _logger.LogDebug(
                "Deboarding: {Deboarded} off, {Remaining} remain (-{Unseated} this update, written {Ok})",
                deboardedCount,
                targetRemaining,
                unseated,
                ok);
        }
    }

    private async Task FinalizeDeboardingAsync()
    {
        if (_deboardMap.Length > 0)
        {
            Array.Clear(_deboardMap);
            await _writer.WriteAsync(
                ProsimDataRefNames.PaxSeatOccupationString,
                SeatMap.Build(_deboardMap)).ConfigureAwait(false);
        }
        await WriteCargoAsync(0).ConfigureAwait(false);
        RecordDecision("deboarding sync", "completed — aircraft empty (pax 0, cargo 0)");
    }

    private async Task WriteBoardedAsync(int boardedCount)
    {
        var seated = SeatMap.FillBoarded(_plannedMap, _boardedMap, boardedCount);
        if (seated > 0 || boardedCount != _lastWrittenBoarded)
        {
            var ok = await _writer.WriteAsync(
                ProsimDataRefNames.PaxSeatOccupationString,
                SeatMap.Build(_boardedMap)).ConfigureAwait(false);
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
    }

    private async Task WriteCargoAsync(double percent)
    {
        var planned = _plannedCargo.GetValue(0.0);
        if (planned <= 0)
        {
            return;
        }

        var loaded = planned * Math.Clamp(percent, 0, 100) / 100.0;
        var (forward, aft) = LoadMath.SplitCargo(
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

    /// <summary>Pushes the pax/cargo counters to the diagnostics store (Flight Status rows).
    /// All reads are cached; publishes only on change, so the idle cost is one record compare
    /// per tick. Null until GSX reports a planned pax total — the page renders "—".</summary>
    private void PublishCounters()
    {
        var target = (int)_plannedTotalLvar.GetValue(0.0);
        var view = target <= 0
            ? null
            : new GsxBoardingCountersView(
                target,
                (int)_boardedLvar.GetValue(0.0),
                (int)_deboardTotal.GetValue(0.0),
                Math.Round(_cargoPercent.GetValue(0.0)),
                Math.Round(_deboardCargoPercent.GetValue(0.0)));
        if (!Equals(view, _lastPublishedCounters))
        {
            _lastPublishedCounters = view;
            _diagnostics.UpdateBoardingCounters(view);
        }
    }

    private void RecordDecision(string action, string reason)
    {
        _logger.LogInformation("GSX {Action}: {Reason}", action, reason);
        _diagnostics.RecordDecision(new GsxDecisionView(DateTimeOffset.UtcNow, action, reason));
    }
}


