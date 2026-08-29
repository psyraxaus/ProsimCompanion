using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Aircraft.Acars;
using ProsimCompanion.Core.Aircraft.Gateway;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Aircraft.WeightAndBalance;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;
using ProsimCompanion.Prosim.Acars;

namespace ProsimCompanion.Prosim.Loadsheet;

/// <summary>
/// The in-house loadsheet pipeline orchestrator (ProSim's Preliminary endpoint is broken for
/// non-A320 variants — docs/integrations/prosim.md §4, so neither ProSim loadsheet endpoint is
/// ever called). Prelim on GSX refuel-active, final after boarding completes plus a crew-realism
/// delay; both write the ProSim-shaped JSON envelope to the EFB dataref first (W&amp;B page
/// renders immediately), settle 3 s, then uplink the ACARS body to fixed slots 01/02.
/// Edition numbers: each prelim (resends included) increments; the final inherits the prelim's.
/// A cycle reset (GSX turnaround) clears the caches and returns the counter to 1.
/// </summary>
public sealed class LoadsheetService : ILoadsheetControl, IDisposable
{
    // A322 operational constants with no dataref (per-variant config is future work).
    private const double DefaultMaxLawKg = 64500.0;
    private const double DefaultWaterWasteKg = 1080.0;
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(3);

    private readonly IProsimGateway _gateway;
    private readonly AcarsUplink _acars;
    private readonly OfpStore _ofpStore;
    private readonly LoadsheetStore _store;
    private readonly GroundOpsSignals _signals;
    private readonly ConnectionStatusStore _connections;
    private readonly IFmsInitSync _fmsSync;
    private readonly IOptionsMonitor<FlightDataOptions> _options;
    private readonly GsxDiagnosticsStore _diagnostics;
    private readonly GsxResyncState _resyncState;
    private readonly ISimVars _simVars;

    // The simulated clock (issue #95): loadsheet timestamps and STD-offset triggers follow
    // the sim's day; falls back to real UTC when the sim clock is not live.
    private readonly ISimClock _simClock;
    private readonly ILogger<LoadsheetService> _logger;

    private readonly IDataRefSubscription<int>[] _zoneAmounts;
    private readonly IDataRefSubscription<int>[] _zoneCapacities;
    private readonly IDataRefSubscription<double> _cargoForward;
    private readonly IDataRefSubscription<double> _cargoAft;
    private readonly IDataRefSubscription<double> _cargoForwardCapacity;
    private readonly IDataRefSubscription<double> _cargoAftCapacity;
    private readonly IDataRefSubscription<double> _cargoBulkCapacity;
    private readonly IDataRefSubscription<double> _fuelCenter;
    private readonly IDataRefSubscription<double> _fuelLeft;
    private readonly IDataRefSubscription<double> _fuelRight;
    private readonly IDataRefSubscription<double> _zfw;
    private readonly IDataRefSubscription<double> _zfwMax;
    private readonly IDataRefSubscription<double> _gross;
    private readonly IDataRefSubscription<double> _grossMax;
    private readonly IDataRefSubscription<double> _cg;
    private readonly IDataRefSubscription<double> _zfwcg;

    private static readonly TimeSpan StdCheckInterval = TimeSpan.FromSeconds(30);

    private readonly object _stateLock = new();
    private readonly Timer _stdTimer;
    private LoadsheetData? _cachedPrelim;
    private LoadsheetContext? _cachedPrelimContext;
    private bool _finalSent;
    private int _nextEditionNumber = 1;
    private CancellationTokenSource? _pendingFinal;
    private CancellationTokenSource? _autoPrelim;
    private ConnectionState _lastProsimState = ConnectionState.Disconnected;
    private bool _restoredOrPrimedThisConnect;

    public LoadsheetService(
        IProsimDataRefs prosim,
        IProsimGateway gateway,
        AcarsUplink acars,
        OfpStore ofpStore,
        LoadsheetStore store,
        GroundOpsSignals signals,
        ConnectionStatusStore connections,
        IFmsInitSync fmsSync,
        IOptionsMonitor<FlightDataOptions> options,
        GsxDiagnosticsStore diagnostics,
        GsxResyncState resyncState,
        ISimVars simVars,
        ISimClock simClock,
        ILogger<LoadsheetService> logger)
    {
        ArgumentNullException.ThrowIfNull(prosim);
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(acars);
        ArgumentNullException.ThrowIfNull(ofpStore);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(fmsSync);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(resyncState);
        ArgumentNullException.ThrowIfNull(simVars);
        ArgumentNullException.ThrowIfNull(simClock);
        ArgumentNullException.ThrowIfNull(logger);
        _resyncState = resyncState;
        _simVars = simVars;
        _simClock = simClock;

        _gateway = gateway;
        _acars = acars;
        _ofpStore = ofpStore;
        _store = store;
        _signals = signals;
        _connections = connections;
        _fmsSync = fmsSync;
        _options = options;
        _diagnostics = diagnostics;
        _logger = logger;

        _zoneAmounts =
        [
            prosim.Subscribe(ProsimDataRefNames.PaxZone1Amount),
            prosim.Subscribe(ProsimDataRefNames.PaxZone2Amount),
            prosim.Subscribe(ProsimDataRefNames.PaxZone3Amount),
            prosim.Subscribe(ProsimDataRefNames.PaxZone4Amount),
        ];
        _zoneCapacities =
        [
            prosim.Subscribe(ProsimDataRefNames.PaxZone1Capacity),
            prosim.Subscribe(ProsimDataRefNames.PaxZone2Capacity),
            prosim.Subscribe(ProsimDataRefNames.PaxZone3Capacity),
            prosim.Subscribe(ProsimDataRefNames.PaxZone4Capacity),
        ];
        _cargoForward = prosim.Subscribe(ProsimDataRefNames.CargoForwardAmount);
        _cargoAft = prosim.Subscribe(ProsimDataRefNames.CargoAftAmount);
        _cargoForwardCapacity = prosim.Subscribe(ProsimDataRefNames.CargoForwardCapacity);
        _cargoAftCapacity = prosim.Subscribe(ProsimDataRefNames.CargoAftCapacity);
        _cargoBulkCapacity = prosim.Subscribe(ProsimDataRefNames.CargoBulkCapacity);
        _fuelCenter = prosim.Subscribe(ProsimDataRefNames.FuelCenter);
        _fuelLeft = prosim.Subscribe(ProsimDataRefNames.FuelLeft);
        _fuelRight = prosim.Subscribe(ProsimDataRefNames.FuelRight);
        _zfw = prosim.Subscribe(ProsimDataRefNames.WeightZfw);
        _zfwMax = prosim.Subscribe(ProsimDataRefNames.WeightZfwMax);
        _gross = prosim.Subscribe(ProsimDataRefNames.WeightGross);
        _grossMax = prosim.Subscribe(ProsimDataRefNames.WeightGrossMax);
        _cg = prosim.Subscribe(ProsimDataRefNames.CenterOfGravity);
        _zfwcg = prosim.Subscribe(ProsimDataRefNames.Zfwcg);

        _signals.RefuelServiceActive += OnRefuelServiceActive;
        _signals.BoardingCompleted += OnBoardingCompleted;
        _signals.FlightCycleReset += ResetCycle;
        _connections.Changed += OnConnectionChanged;
        _resyncState.Assessed += OnResyncAssessed;

        // STD-offset trigger (Prosim2GSX's timing model, opt-in): a slow poll rather than a
        // scheduled one-shot because the STD source can change at any time (new OFP import,
        // manual entry on the Loadsheet page).
        _stdTimer = new Timer(_ => OnStdTick(), null, StdCheckInterval, StdCheckInterval);
    }

    public void Dispose()
    {
        _stdTimer.Dispose();
        _signals.RefuelServiceActive -= OnRefuelServiceActive;
        _signals.BoardingCompleted -= OnBoardingCompleted;
        _signals.FlightCycleReset -= ResetCycle;
        _connections.Changed -= OnConnectionChanged;
        _resyncState.Assessed -= OnResyncAssessed;
        _pendingFinal?.Cancel();
        _pendingFinal?.Dispose();
        _autoPrelim?.Cancel();
        _autoPrelim?.Dispose();

        foreach (var sub in _zoneAmounts.Concat<IDataRefSubscription>(_zoneCapacities))
        {
            sub.Dispose();
        }
        _cargoForward.Dispose();
        _cargoAft.Dispose();
        _cargoForwardCapacity.Dispose();
        _cargoAftCapacity.Dispose();
        _cargoBulkCapacity.Dispose();
        _fuelCenter.Dispose();
        _fuelLeft.Dispose();
        _fuelRight.Dispose();
        _zfw.Dispose();
        _zfwMax.Dispose();
        _gross.Dispose();
        _grossMax.Dispose();
        _cg.Dispose();
        _zfwcg.Dispose();
    }

    // ── Automatic triggers ───────────────────────────────────────────────────────────────

    /// <summary>One automatic prelim per flight cycle (the predecessor guard); manual
    /// generate/resend calls bypass this and increment the edition number instead.</summary>
    private void OnRefuelServiceActive()
    {
        if (!_options.CurrentValue.AutoPrelimOnRefuel)
        {
            return;
        }

        lock (_stateLock)
        {
            if (_cachedPrelim is not null)
            {
                return;
            }
        }

        StartAutomaticPrelim("refuel active");
    }

    /// <summary>STD-offset prelim trigger: fires once the clock passes STD minus the configured
    /// offset. STD = the Loadsheet page's manual override when set, else the OFP's scheduled
    /// out. Shares the one-automatic-prelim-per-cycle guard with the refuel trigger.</summary>
    private void OnStdTick()
    {
        var options = _options.CurrentValue;
        if (!options.AutoPrelimAtStd)
        {
            return;
        }

        var std = EffectiveStdUtc();
        if (std is null || _simClock.UtcNowOrReal < std - TimeSpan.FromMinutes(Math.Max(0, options.PrelimStdOffsetMinutes)))
        {
            return;
        }

        lock (_stateLock)
        {
            if (_cachedPrelim is not null)
            {
                return;
            }
        }

        RecordDecision($"STD {std:HH:mm}Z minus {options.PrelimStdOffsetMinutes} min reached — preliminary loadsheet");
        StartAutomaticPrelim("STD offset");
    }

    /// <summary>Arms the automatic prelim: the edge that fired it (refuel active, tankering
    /// pre-skip, STD offset) can arrive before the prelim's inputs exist — the OFP not yet
    /// imported, or the CG datarefs not yet pushed after an app restart (2026-08-29
    /// turnaround: ERROR on the page until a manual Resend). The armed trigger re-checks on
    /// <see cref="PrelimTriggerPolicy.RetryInterval"/> and generates once ready; a manual
    /// generate in the meantime wins (the cached prelim ends the wait). Re-arming replaces
    /// any earlier wait; the cycle reset cancels it.</summary>
    private void StartAutomaticPrelim(string trigger)
    {
        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _autoPrelim, cts);
        previous?.Cancel();
        previous?.Dispose();
        _ = Task.Run(() => RunAutomaticPrelimAsync(trigger, cts.Token));
    }

    private async Task RunAutomaticPrelimAsync(string trigger, CancellationToken cancellationToken)
    {
        var armedAt = DateTimeOffset.UtcNow;
        string? lastReason = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                lock (_stateLock)
                {
                    if (_cachedPrelim is not null)
                    {
                        return; // a manual generate/resend produced it meanwhile
                    }
                }

                var decision = PrelimTriggerPolicy.Next(ReadPrelimReadiness(), DateTimeOffset.UtcNow - armedAt);
                switch (decision.Action)
                {
                    case PrelimTriggerAction.Generate:
                        if (lastReason is not null)
                        {
                            RecordDecision($"automatic prelim ({trigger}) — inputs ready, generating");
                        }

                        if (await GeneratePreliminaryAsync(cancellationToken).ConfigureAwait(false))
                        {
                            return;
                        }

                        // A transient failure (EFB write, ACARS uplink) is retried like a
                        // missing input; the slot keeps the failure text as the wait reason.
                        var failure = _store.Snapshot().Prelim.Error ?? "generation failed";
                        SetWaiting(trigger, $"retrying — {failure}", ref lastReason);
                        break;

                    case PrelimTriggerAction.Wait:
                        SetWaiting(trigger, decision.Reason!, ref lastReason);
                        break;

                    case PrelimTriggerAction.GiveUp:
                        RecordDecision($"automatic prelim ({trigger}) {decision.Reason} — press Resend on the Loadsheet page once the inputs are there");
                        _store.SetPrelim(LoadsheetSnapshot.EmptySlot with
                        {
                            Status = LoadsheetSlotStatus.Failed,
                            Error = decision.Reason,
                        });
                        return;
                }

                await Task.Delay(PrelimTriggerPolicy.RetryInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Cycle reset or a newer trigger replaced this wait.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Automatic prelim ({Trigger}) failed unexpectedly", trigger);
        }
    }

    private void SetWaiting(string trigger, string reason, ref string? lastReason)
    {
        if (reason == lastReason)
        {
            return;
        }

        lastReason = reason;
        RecordDecision($"automatic prelim ({trigger}) {reason}");
        _store.SetPrelim(LoadsheetSnapshot.EmptySlot with
        {
            Status = LoadsheetSlotStatus.Waiting,
            Error = reason,
        });
    }

    private PrelimReadiness ReadPrelimReadiness() => new(
        OfpImported: _ofpStore.Current is not null,
        CgPopulated: _cg.RawValue is not null && _zfwcg.RawValue is not null,
        GrossCgMac: _cg.Value,
        ZfwCgMac: _zfwcg.Value);

    /// <summary>Manual override wins over the OFP. A manual time-of-day is anchored to today
    /// (UTC); one that already passed by more than 12 h is read as tomorrow's departure so an
    /// evening entry for an after-midnight flight doesn't fire instantly.</summary>
    private DateTimeOffset? EffectiveStdUtc()
    {
        if (_store.Snapshot().StdOverrideUtc is { } manual)
        {
            // Anchored to the SIMULATED day (issue #95): a pilot flying an overnight sim at a
            // real-world afternoon enters the sim's departure time, not the wall clock's.
            var now = _simClock.UtcNowOrReal;
            var today = new DateTimeOffset(now.UtcDateTime.Date.Add(manual.ToTimeSpan()), TimeSpan.Zero);
            return now - today > TimeSpan.FromHours(12) ? today.AddDays(1) : today;
        }
        return _ofpStore.Current?.ScheduledOutUtc;
    }

    private void OnBoardingCompleted()
    {
        var options = _options.CurrentValue;
        if (!options.AutoFinalOnBoardingComplete)
        {
            return;
        }

        lock (_stateLock)
        {
            if (_cachedPrelim is null)
            {
                RecordDecision("no prelim cached at boarding-complete — final loadsheet skipped (generate a prelim first)");
                return;
            }
            if (_finalSent)
            {
                return;
            }
        }

        // Crew-realism delay: the dispatcher "finalizes figures" before the final arrives.
        var minSeconds = Math.Max(0, options.FinalDelayMinSeconds);
        var maxSeconds = Math.Max(minSeconds + 1, options.FinalDelayMaxSeconds);
        var delay = TimeSpan.FromSeconds(Random.Shared.Next(minSeconds, maxSeconds));

        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _pendingFinal, cts);
        previous?.Cancel();
        previous?.Dispose();

        RecordDecision($"boarding complete — final loadsheet in {delay.TotalSeconds:F0} s");
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, cts.Token).ConfigureAwait(false);
                await GenerateFinalAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cycle reset or shutdown — silently dropped.
            }
        });
    }

    /// <summary>On every ProSim connect the loadsheet datarefs are either RESTORED or primed
    /// empty — decided by the startup resync (issue #30). The predecessor always primed empty,
    /// which erased the loadsheet the crew already had after a mid-turnaround app restart.
    /// Restore/prime waits for BOTH the connect and the resync assessment (either may come
    /// first), and runs once per connect.</summary>
    private void OnConnectionChanged(object? sender, EventArgs e)
    {
        var state = _connections.Snapshot().FirstOrDefault(pair => pair.Key == Subsystems.Prosim).Value;
        if (state == _lastProsimState)
        {
            return;
        }
        _lastProsimState = state;
        if (state != ConnectionState.Connected)
        {
            _restoredOrPrimedThisConnect = false;
            return;
        }

        TryRestoreOrPrime();
    }

    private void OnResyncAssessed() => TryRestoreOrPrime();

    private void TryRestoreOrPrime()
    {
        if (_restoredOrPrimedThisConnect
            || !_resyncState.IsAssessed
            || _lastProsimState != ConnectionState.Connected)
        {
            return;
        }

        _restoredOrPrimedThisConnect = true;
        _ = Task.Run(RestoreOrPrimeAsync);
    }

    private async Task RestoreOrPrimeAsync()
    {
        try
        {
            if (_resyncState.LoadsheetPrelimEdition > 0 && await TryRestoreAsync().ConfigureAwait(false))
            {
                return;
            }

            // Fresh cycle (or unrecoverable content): last session's loadsheet must not appear
            // on this session's W&B page — the datarefs only exist after a first write.
            var ok = await _gateway.WriteDataRefAsync(ProsimDataRefNames.EfbPrelimLoadsheet, "").ConfigureAwait(false)
                & await _gateway.WriteDataRefAsync(ProsimDataRefNames.EfbFinalLoadsheet.Name, "").ConfigureAwait(false);
            _logger.LogInformation("Loadsheet datarefs primed with empty placeholders (ok={Ok})", ok);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loadsheet restore-or-prime failed");
        }
    }

    /// <summary>Reads the prelim envelope back from the EFB dataref and restores the cycle
    /// state the previous process held in memory: cached prelim (final generation needs its
    /// trip fuel and CHK baselines), edition counter, final-sent flag and the store slots.
    /// When boarding is proven complete and the final has not gone out yet, the automatic
    /// final is re-armed exactly as a live boarding-complete would have.</summary>
    private async Task<bool> TryRestoreAsync()
    {
        var prelimJson = await _gateway.QueryDataRefAsync(ProsimDataRefNames.EfbPrelimLoadsheet).ConfigureAwait(false);
        if (!LoadsheetEnvelope.TryParse(prelimJson, out _, out var prelimData, out var prelimCtx))
        {
            RecordDecision(
                $"tracking LVARs promised prelim EDNO {_resyncState.LoadsheetPrelimEdition} but the EFB dataref "
                + "held no parseable loadsheet — starting the cycle fresh");
            return false;
        }

        lock (_stateLock)
        {
            _cachedPrelim = prelimData;
            _cachedPrelimContext = prelimCtx;
            _finalSent = _resyncState.LoadsheetFinalSent;
            _nextEditionNumber = prelimCtx.EditionNumber + 1;
        }

        _store.SetPrelim(LoadsheetStore.SlotFrom(
            LoadsheetSlotStatus.Sent, prelimCtx.EditionNumber, prelimData, new DateTimeOffset(prelimCtx.Time, TimeSpan.Zero)));
        RecordDecision($"restored prelim EDNO {prelimCtx.EditionNumber} from the EFB dataref after restart");

        if (_resyncState.LoadsheetFinalSent)
        {
            var finalJson = await _gateway.QueryDataRefAsync(ProsimDataRefNames.EfbFinalLoadsheet.Name).ConfigureAwait(false);
            if (LoadsheetEnvelope.TryParse(finalJson, out var isFinal, out var finalData, out var finalCtx) && isFinal)
            {
                _store.SetFinal(LoadsheetStore.SlotFrom(
                    LoadsheetSlotStatus.Sent, finalCtx.EditionNumber, finalData, new DateTimeOffset(finalCtx.Time, TimeSpan.Zero)));
                RecordDecision($"restored final EDNO {finalCtx.EditionNumber} from the EFB dataref after restart");
            }
        }
        else if (_resyncState.BoardingProven)
        {
            RecordDecision("boarding already complete at restart and no final sent — re-arming the automatic final");
            OnBoardingCompleted();
        }

        return true;
    }

    // ── ILoadsheetControl ────────────────────────────────────────────────────────────────

    public async Task<bool> GeneratePreliminaryAsync(CancellationToken cancellationToken = default)
    {
        var ofp = _ofpStore.Current;
        if (ofp is null)
        {
            RecordDecision("no OFP imported — cannot generate a preliminary loadsheet");
            _store.SetPrelim(LoadsheetSnapshot.EmptySlot with
            {
                Status = LoadsheetSlotStatus.Failed,
                Error = "No OFP imported",
            });
            return false;
        }

        var ctx = BuildContext(ofp);
        lock (_stateLock)
        {
            ctx.EditionNumber = _nextEditionNumber;
            _nextEditionNumber++;
        }
        FillDispatcherIfBlank(ctx);

        var plan = new FlightPlanInputs
        {
            PassengerCount = ofp.PaxCount,
            CargoTotalKg = ofp.CargoKg,
            PlannedFuelKg = ofp.FuelPlanRampKg,
            PlannedLandingFuelKg = ofp.FuelPlanLandingKg,
            TaxiFuelKg = ofp.FuelTaxiKg,
            EstimatedZeroFuelWeightKg = ofp.EstZfwKg,
            EstimatedTakeoffWeightKg = ofp.EstTowKg,
            EstimatedLandingWeightKg = ofp.EstLdwKg,
        };

        LoadsheetData data;
        try
        {
            data = A320WeightAndBalance.CalculatePreliminary(plan, ReadLiveState());
        }
        catch (InvalidOperationException ex)
        {
            RecordDecision($"prelim aborted: {ex.Message}");
            _store.SetPrelim(LoadsheetSnapshot.EmptySlot with
            {
                Status = LoadsheetSlotStatus.Failed,
                Error = ex.Message,
            });
            return false;
        }

        _store.SetPrelim(LoadsheetStore.SlotFrom(LoadsheetSlotStatus.Generating, ctx.EditionNumber, data, null));

        var json = LoadsheetEnvelope.Build(data, ctx, isFinal: false, prelimForChk: null);
        if (!await _gateway.WriteDataRefAsync(ProsimDataRefNames.EfbPrelimLoadsheet, json, cancellationToken).ConfigureAwait(false))
        {
            RecordDecision("prelim loadsheet dataref write failed — see gateway log");
            _store.SetPrelim(LoadsheetStore.SlotFrom(LoadsheetSlotStatus.Failed, ctx.EditionNumber, data, null, "EFB write failed"));
            return false;
        }

        // Settle so the cockpit/EFB correlates the incoming ACARS with the displayed sheet
        // (mirrors ProSim's own 3 s wait between dataref write and SendLoadsheet).
        await Task.Delay(SettleDelay, cancellationToken).ConfigureAwait(false);

        var sent = await _acars.SendAsync(new AcarsMessage
        {
            Type = "LOADSHEET",
            Header = "LOADSHEET",
            Id = "01",
            Accept = true,
            Content = LoadsheetFormatter.FormatPreliminary(ctx, data),
        }, cancellationToken).ConfigureAwait(false);

        lock (_stateLock)
        {
            _cachedPrelim = data;
            _cachedPrelimContext = ctx;
        }

        // Tracking LVAR (issue #30): a restart can now tell a prelim already exists this cycle.
        _ = WriteTrackingLvarAsync(CompanionLvarNames.LoadsheetPrelimEdition.Name, ctx.EditionNumber);

        _store.SetPrelim(LoadsheetStore.SlotFrom(
            sent ? LoadsheetSlotStatus.Sent : LoadsheetSlotStatus.Failed,
            ctx.EditionNumber, data, DateTimeOffset.UtcNow,
            sent ? null : "ACARS uplink failed (loadsheet still displayed in the EFB)"));
        RecordDecision(
            $"PRELIM EDNO {ctx.EditionNumber} sent: ZFW {data.ZeroFuelWeight:F0} ({data.ZeroFuelWeightMac:F1}%MAC), " +
            $"TOW {data.TakeoffWeight:F0} ({data.TakeoffWeightMac:F1}%MAC), pax {data.TotalPassengers}, fuel {data.FuelWeight:F0} kg");
        return true;
    }

    public async Task<bool> GenerateFinalAsync(CancellationToken cancellationToken = default)
    {
        LoadsheetData prelim;
        LoadsheetContext prelimContext;
        lock (_stateLock)
        {
            if (_cachedPrelim is null || _cachedPrelimContext is null)
            {
                RecordDecision("cannot generate final loadsheet: no preliminary cached this cycle");
                return false;
            }
            prelim = _cachedPrelim;
            prelimContext = _cachedPrelimContext;
        }

        // The final inherits the prelim's edition, dispatcher and route metadata so the
        // cockpit's REVISIONS / COMPLIANCE header references the right edition.
        var ofp = _ofpStore.Current;
        var ctx = ofp is null ? new LoadsheetContext() : BuildContext(ofp);
        ctx.EditionNumber = prelimContext.EditionNumber;
        ctx.DispatcherFirstName = prelimContext.DispatcherFirstName;
        ctx.DispatcherLastName = prelimContext.DispatcherLastName;
        if (string.IsNullOrEmpty(ctx.Callsign))
        {
            ctx.Callsign = prelimContext.Callsign;
        }
        if (string.IsNullOrEmpty(ctx.Ident))
        {
            ctx.Ident = prelimContext.Ident;
        }
        if (string.IsNullOrEmpty(ctx.DepartureIata))
        {
            ctx.DepartureIata = prelimContext.DepartureIata;
        }
        if (string.IsNullOrEmpty(ctx.ArrivalIata))
        {
            ctx.ArrivalIata = prelimContext.ArrivalIata;
        }
        if (string.IsNullOrEmpty(ctx.AircraftTailNumber))
        {
            ctx.AircraftTailNumber = prelimContext.AircraftTailNumber;
        }
        if (string.IsNullOrEmpty(ctx.ScheduledDepartureTime))
        {
            ctx.ScheduledDepartureTime = prelimContext.ScheduledDepartureTime;
        }
        if (ctx.MaxZfwKg == 0)
        {
            ctx.MaxZfwKg = prelimContext.MaxZfwKg;
        }
        if (ctx.MaxTowKg == 0)
        {
            ctx.MaxTowKg = prelimContext.MaxTowKg;
        }
        if (ctx.MaxLawKg == 0)
        {
            ctx.MaxLawKg = prelimContext.MaxLawKg;
        }
        if (ctx.WaterWasteKg == 0)
        {
            ctx.WaterWasteKg = prelimContext.WaterWasteKg;
        }

        LoadsheetData data;
        try
        {
            data = A320WeightAndBalance.CalculateFinal(ReadLiveState());
        }
        catch (InvalidOperationException ex)
        {
            RecordDecision($"final aborted: {ex.Message}");
            _store.SetFinal(LoadsheetSnapshot.EmptySlot with
            {
                Status = LoadsheetSlotStatus.Failed,
                Error = ex.Message,
            });
            return false;
        }

        // FINAL LAW = final TOW − planned trip fuel. Trip fuel is a dispatch-time quantity —
        // recover it from the cached prelim rather than re-measuring (predecessor rule; without
        // this, LAW == TOW, TIF computes to 0 and UNDLD understates by the whole burn).
        var prelimTripFuelKg = prelim.TakeoffWeight - prelim.LandingWeight;
        if (prelimTripFuelKg > 0)
        {
            data.LandingWeight = data.TakeoffWeight - prelimTripFuelKg;
        }
        else
        {
            _logger.LogWarning(
                "Cached prelim trip fuel non-positive ({Trip:F0} kg); final landing weight stays at TOW",
                prelimTripFuelKg);
        }

        _store.SetFinal(LoadsheetStore.SlotFrom(LoadsheetSlotStatus.Generating, ctx.EditionNumber, data, null));

        var json = LoadsheetEnvelope.Build(data, ctx, isFinal: true, prelimForChk: prelim);
        if (!await _gateway.WriteDataRefAsync(ProsimDataRefNames.EfbFinalLoadsheet.Name, json, cancellationToken).ConfigureAwait(false))
        {
            RecordDecision("final loadsheet dataref write failed — see gateway log");
            _store.SetFinal(LoadsheetStore.SlotFrom(LoadsheetSlotStatus.Failed, ctx.EditionNumber, data, null, "EFB write failed"));
            return false;
        }

        await Task.Delay(SettleDelay, cancellationToken).ConfigureAwait(false);

        var sent = await _acars.SendAsync(new AcarsMessage
        {
            Type = "LOADSHEET",
            Header = "LOADSHEET",
            Id = "02",
            Accept = true,
            Content = LoadsheetFormatter.FormatFinal(ctx, prelim, data),
        }, cancellationToken).ConfigureAwait(false);

        lock (_stateLock)
        {
            _finalSent = true;
        }

        _ = WriteTrackingLvarAsync(CompanionLvarNames.LoadsheetFinalSent.Name, 1);

        _store.SetFinal(LoadsheetStore.SlotFrom(
            sent ? LoadsheetSlotStatus.Sent : LoadsheetSlotStatus.Failed,
            ctx.EditionNumber, data, DateTimeOffset.UtcNow,
            sent ? null : "ACARS uplink failed (loadsheet still displayed in the EFB)"));
        RecordDecision(
            $"FINAL EDNO {ctx.EditionNumber} sent: ZFW {data.ZeroFuelWeight:F0} ({data.ZeroFuelWeightMac:F1}%MAC), " +
            $"TOW {data.TakeoffWeight:F0} ({data.TakeoffWeightMac:F1}%MAC), pax {data.TotalPassengers}, fuel {data.FuelWeight:F0} kg");

        if (sent && _options.CurrentValue.AutoSyncFmsOnFinal)
        {
            var result = await _fmsSync.SyncAsync(cancellationToken).ConfigureAwait(false);
            RecordDecision(result is null
                ? "auto FMS sync after final failed — see log"
                : $"auto FMS sync after final: ZFW {result.ZfwTonnes:F1} t, ZFWCG {result.ZfwCgPercent:F1}%, block {result.BlockTonnes:F1} t ({result.Source})");
        }

        return true;
    }

    public void ResetCycle()
    {
        var pending = Interlocked.Exchange(ref _pendingFinal, null);
        pending?.Cancel();
        pending?.Dispose();
        var autoPrelim = Interlocked.Exchange(ref _autoPrelim, null);
        autoPrelim?.Cancel();
        autoPrelim?.Dispose();

        lock (_stateLock)
        {
            _cachedPrelim = null;
            _cachedPrelimContext = null;
            _finalSent = false;
            _nextEditionNumber = 1;
        }
        _store.Reset();
        _ = WriteTrackingLvarAsync(CompanionLvarNames.LoadsheetPrelimEdition.Name, 0);
        _ = WriteTrackingLvarAsync(CompanionLvarNames.LoadsheetFinalSent.Name, 0);
        RecordDecision("loadsheet state reset for new flight cycle");
    }

    /// <summary>Best-effort tracking-LVAR write — MSFS may be absent (degrade, not fail).</summary>
    private async Task WriteTrackingLvarAsync(string name, double value)
    {
        try
        {
            await _simVars.WriteAsync(name, value).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogDebug("Tracking LVAR write {Name}={Value} skipped: {Reason}", name, value, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tracking LVAR write {Name}={Value} failed", name, value);
        }
    }

    // ── Internals ────────────────────────────────────────────────────────────────────────

    /// <summary>Max weights prefer live datarefs (what the simulator's W&amp;B physics actually
    /// enforces), falling back to OFP values; max landing weight has no dataref — SimBrief,
    /// then the A322 default.</summary>
    private LoadsheetContext BuildContext(OfpData ofp)
    {
        var maxZfw = _zfwMax.Value;
        if (maxZfw <= 0)
        {
            maxZfw = ofp.MaxZfwKg;
        }
        var maxTow = _grossMax.Value;
        if (maxTow <= 0)
        {
            maxTow = ofp.MaxTowKg;
        }

        return new LoadsheetContext
        {
            Ident = ofp.Ident,
            Callsign = ofp.Callsign,
            DepartureIata = ofp.OriginIata,
            ArrivalIata = ofp.DestinationIata,
            AircraftTailNumber = ofp.AircraftReg,
            // Sim time (issue #95): the footer's HH:mmZ must match the simulated day the
            // pilot is flying, not the wall clock behind the curtain.
            Time = _simClock.UtcNowOrReal.UtcDateTime,
            ScheduledDepartureTime = ofp.ScheduledOutUtc is { } schedOut
                ? schedOut.UtcDateTime.ToString("d dMMMyy", CultureInfo.InvariantCulture)
                : "",
            MaxZfwKg = maxZfw,
            MaxTowKg = maxTow,
            MaxLawKg = ofp.MaxLdwKg > 0 ? ofp.MaxLdwKg : DefaultMaxLawKg,
            WaterWasteKg = DefaultWaterWasteKg,
        };
    }

    private WeightAndBalanceLiveState ReadLiveState() => new()
    {
        ZoneCapacities = [.. _zoneCapacities.Select(zone => zone.Value)],
        ZoneAmounts = [.. _zoneAmounts.Select(zone => zone.Value)],
        CargoForwardCapacityKg = _cargoForwardCapacity.Value,
        CargoAftCapacityKg = _cargoAftCapacity.Value,
        CargoBulkCapacityKg = _cargoBulkCapacity.Value,
        CargoForwardKg = _cargoForward.Value,
        CargoAftKg = _cargoAft.Value,
        FuelCenterKg = _fuelCenter.Value,
        FuelLeftKg = _fuelLeft.Value,
        FuelRightKg = _fuelRight.Value,
        ZfwKg = _zfw.Value,
        GrossWeightKg = _gross.Value,
        GrossCgMac = _cg.Value,
        ZfwCgMac = _zfwcg.Value,
    };

    private static void FillDispatcherIfBlank(LoadsheetContext ctx)
    {
        if (!string.IsNullOrEmpty(ctx.DispatcherFirstName) && !string.IsNullOrEmpty(ctx.DispatcherLastName))
        {
            return;
        }

        var (first, last) = DispatcherNamePool.GetRandom();
        if (string.IsNullOrEmpty(ctx.DispatcherFirstName))
        {
            ctx.DispatcherFirstName = first;
        }
        if (string.IsNullOrEmpty(ctx.DispatcherLastName))
        {
            ctx.DispatcherLastName = last;
        }
    }

    private void RecordDecision(string reason)
    {
        _logger.LogInformation("Loadsheet: {Reason}", reason);
        _diagnostics.RecordDecision(new GsxDecisionView(DateTimeOffset.UtcNow, "loadsheet", reason));
    }
}
