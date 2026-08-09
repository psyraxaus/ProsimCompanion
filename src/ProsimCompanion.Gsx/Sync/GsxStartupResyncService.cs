using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;
using ProsimCompanion.Gsx.Services;

namespace ProsimCompanion.Gsx.Sync;

/// <summary>Everything the startup assessment can observe, gathered by the service so the
/// verdict logic stays pure and testable (issue #30). <see cref="FlightPlanLoaded"/> is the
/// corroboration gate (issue #33): SimBrief OFP imported OR a valid MCDU origin/destination
/// pair.</summary>
public sealed record ResyncEvidence(
    bool TurnaroundLvar,
    bool PrepDoneLvar,
    IReadOnlySet<string> ServiceDoneLvars,
    double FuelTargetKg,
    double FuelOnBoardKg,
    int PaxBooked,
    int PaxOccupied,
    string? EfbBoardingStatus,
    int LoadsheetPrelimEdition,
    bool LoadsheetFinalSent,
    IReadOnlyList<string> ConfiguredOneShotServices,
    bool FlightPlanLoaded = true,
    bool ProsimDataAvailable = true);

/// <summary>What the assessment decided: services to seed as completed (with the evidence that
/// proved — or the policy that assumed — each one), plus the recovered cross-cutting flags.
/// <see cref="BoardingProven"/> is boarding completion backed by actual evidence (pax counts,
/// EFB status, tracking LVAR) as opposed to the never-re-call assumption — only proven
/// boarding may re-arm the automatic final loadsheet.</summary>
public sealed record ResyncVerdict(
    IReadOnlyList<(string ServiceId, string Reason)> SeedCompleted,
    bool Turnaround,
    bool SeedPrepComplete,
    bool BoardingProven,
    int LoadsheetPrelimEdition,
    bool LoadsheetFinalSent)
{
    /// <summary>True when any prior departure-flow progress was detected at all.</summary>
    public bool DepartureInProgress => SeedCompleted.Count > 0;

    /// <summary>The tracking LVARs claimed departure progress the datarefs contradict
    /// (issue #33: LVARs survive an app restart AND a ProSim reset, but a reset invalidates
    /// them) — the service must clear them so they cannot confuse a later restart.</summary>
    public bool StaleTrackingDetected { get; init; }
}

/// <summary>
/// The pure startup-resync verdict (issue #30). Evidence order per the agreed design:
/// dataref-first (fuel balance, pax counts — live truths that survive every restart), then the
/// companion's own tracking LVARs (which survive an app restart and self-invalidate with the
/// sim session). Ambiguity policy is NEVER RE-CALL: once anything proves this leg's departure
/// flow already progressed, the remaining one-shot services are assumed done rather than
/// re-called — a duplicate catering truck is worse than a manual call, and every service stays
/// manually callable. With no progress evidence at all the verdict is empty and a fresh
/// departure flow runs normally.
/// </summary>
public static class GsxStartupResync
{
    /// <summary>FOB tolerance for "refuel already done". Deliberately looser than the runtime
    /// fuel-sync variance: APU burn while the app was down must not un-prove a completed
    /// refuel.</summary>
    public const double FuelVarianceKg = 100.0;

    /// <summary>Proven completions that do NOT indicate departure-services progress and so
    /// never trigger the never-re-call assumption: deboarding belongs to the ARRIVAL flow and
    /// the GPU connects during ground prep — both complete before a fresh leg's departure
    /// services have run, and treating them as progress would wrongly skip all of leg 2.</summary>
    private static readonly HashSet<string> ProgressExempt =
        new(StringComparer.OrdinalIgnoreCase) { GsxServiceIds.Deboarding, GsxServiceIds.Gpu };

    public static ResyncVerdict Assess(ResyncEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        // Corroboration gate (issue #33): the tracking LVARs survive an app restart AND a
        // ProSim reset, but a reset invalidates them without clearing them. A departure-flow
        // claim (any non-exempt service done, or a prelim sent) is only believed when the
        // datarefs agree: a flight plan must be loaded (the sequencer is plan-gated, so
        // departure claims without one are always stale), a claimed refuel must not be
        // contradicted by the live fuel state, and a claimed boarding by an empty cabin.
        // Exempt-only claims (Deboarding after arrival) stay trusted — the legit
        // restart-before-the-next-plan window has exactly that shape.
        var claimsDeparture = evidence.LoadsheetPrelimEdition > 0
            || evidence.ServiceDoneLvars.Any(id => !ProgressExempt.Contains(id));
        var refuelContradicted = evidence.ServiceDoneLvars.Contains(GsxServiceIds.Refueling)
            && evidence.FuelTargetKg > FuelVarianceKg
            && evidence.FuelOnBoardKg < evidence.FuelTargetKg - FuelVarianceKg;
        var boardingContradicted = evidence.ServiceDoneLvars.Contains(GsxServiceIds.Boarding)
            && evidence.PaxOccupied == 0;
        // Never judge staleness on absent data: with ProSim disconnected every dataref reads
        // as "nothing", which must not condemn valid LVARs (degrade, not fail).
        var stale = evidence.ProsimDataAvailable
            && claimsDeparture
            && (!evidence.FlightPlanLoaded || refuelContradicted || boardingContradicted);

        var trustedLvars = stale
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : evidence.ServiceDoneLvars;
        var prelimEdition = stale ? 0 : evidence.LoadsheetPrelimEdition;

        var seeds = new List<(string ServiceId, string Reason)>();
        var proven = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Prove(string serviceId, string reason)
        {
            if (proven.Add(serviceId))
            {
                seeds.Add((serviceId, reason));
            }
        }

        foreach (var serviceId in trustedLvars)
        {
            Prove(serviceId, "tracking LVAR says completed this cycle");
        }

        if (evidence.FuelTargetKg > FuelVarianceKg
            && evidence.FuelOnBoardKg >= evidence.FuelTargetKg - FuelVarianceKg)
        {
            Prove(
                GsxServiceIds.Refueling,
                $"fuel on board {evidence.FuelOnBoardKg:F0} kg meets the {evidence.FuelTargetKg:F0} kg target");
        }

        if (evidence.PaxBooked > 0 && evidence.PaxOccupied >= evidence.PaxBooked)
        {
            Prove(GsxServiceIds.Boarding, $"{evidence.PaxOccupied} of {evidence.PaxBooked} booked pax aboard");
        }
        else if (evidence.EfbBoardingStatus is "completed" or "ended")
        {
            Prove(GsxServiceIds.Boarding, $"EFB boarding status reads '{evidence.EfbBoardingStatus}'");
        }

        if (prelimEdition > 0)
        {
            // The prelim fires when refuel goes active — it proves the refuel cycle started,
            // and with the fuel target long since reached-or-abandoned we treat it as done.
            Prove(GsxServiceIds.Refueling, $"preliminary loadsheet EDNO {prelimEdition} was already sent");
        }

        var boardingProven = proven.Contains(GsxServiceIds.Boarding);

        // Never re-call: once DEPARTURE progress is proven for this leg, the unproven
        // one-shots are assumed done. Deboarding/GPU proofs are exempt — they complete before
        // a fresh leg's departure services, so a restart right after arrival or ground prep
        // still sequences leg 2 normally.
        if (seeds.Any(seed => !ProgressExempt.Contains(seed.ServiceId)))
        {
            foreach (var serviceId in evidence.ConfiguredOneShotServices)
            {
                if (!GsxServiceIds.IsToggle(serviceId) && !proven.Contains(serviceId))
                {
                    Prove(serviceId, "unverified after restart — never re-called by policy (manual call remains available)");
                }
            }
        }

        // A stale-detected reset also invalidates the sim-session flags: ground prep wrote
        // PROSIM datarefs (chocks/ground power) that the reset cleared, and the user's fresh
        // flight must not inherit turnaround constraints.
        return new ResyncVerdict(
            seeds,
            !stale && evidence.TurnaroundLvar,
            !stale && evidence.PrepDoneLvar,
            boardingProven,
            prelimEdition,
            !stale && evidence.LoadsheetFinalSent)
        {
            StaleTrackingDetected = stale,
        };
    }
}

/// <summary>
/// Owns the companion tracking LVARs and the one-shot startup resync (issue #30). Forward
/// direction: lifecycle completions, the turnaround boundary and ground-prep completion are
/// written into <see cref="CompanionLvarNames"/> LVARs as they happen. Startup direction: once
/// GSX's world is known (Ready + populated mirror), the LVARs and the restart-surviving
/// datarefs are assessed ONCE, proven/assumed services are seeded into the lifecycle tracker,
/// ground prep is fast-forwarded, and <see cref="GsxResyncState"/> is published — the
/// departure sequencer holds until then. If GSX never comes up the assessment times out and
/// reports "nothing detected" so automation is never deadlocked (degrade, not fail).
/// </summary>
public sealed class GsxStartupResyncService : IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AssessmentTimeout = TimeSpan.FromSeconds(90);

    /// <summary>The one-shot services whose done-LVARs are subscribed up front (config can
    /// reorder/skip them, but the id vocabulary is fixed by GSX).</summary>
    private static readonly string[] TrackedServices =
    [
        GsxServiceIds.Refueling, GsxServiceIds.Catering, GsxServiceIds.Boarding,
        GsxServiceIds.Deboarding, GsxServiceIds.Water, GsxServiceIds.Lavatory,
        GsxServiceIds.Cleaning, GsxServiceIds.Gpu, GsxServiceIds.DeIce,
    ];

    private readonly IGsxRemoteApi _api;
    private readonly GsxServiceLifecycleTracker _lifecycle;
    private readonly GsxGroundPrepCoordinator _groundPrep;
    private readonly ISimVars _simVars;
    private readonly GsxResyncState _resyncState;
    private readonly GroundOpsSignals _signals;
    private readonly IOptionsMonitor<GsxOptions> _options;
    private readonly GsxDiagnosticsStore _diagnostics;
    private readonly ILogger<GsxStartupResyncService> _logger;

    private readonly Dictionary<string, IDataRefSubscription> _serviceDoneLvars =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly IDataRefSubscription _turnaroundLvar;
    private readonly IDataRefSubscription _prepDoneLvar;
    private readonly IDataRefSubscription _loadsheetPrelimLvar;
    private readonly IDataRefSubscription _loadsheetFinalLvar;
    private readonly IDataRefSubscription _fuelTargetKg;
    private readonly IDataRefSubscription _fuelTarget;
    private readonly IDataRefSubscription _fuelTotal;
    private readonly IDataRefSubscription _paxBooked;
    private readonly IDataRefSubscription _paxOccupied;
    private readonly IDataRefSubscription _efbBoardingStatus;
    private readonly IDataRefSubscription _ofpImported;
    private readonly IDataRefSubscription _fmsOrigin;
    private readonly IDataRefSubscription _fmsDestination;

    private readonly Timer _timer;
    private readonly long _startedAtTicks = Environment.TickCount64;
    private bool _prepLvarLatched;

    public GsxStartupResyncService(
        IGsxRemoteApi api,
        GsxServiceLifecycleTracker lifecycle,
        GsxGroundPrepCoordinator groundPrep,
        ISimVars simVars,
        IProsimDataRefs prosim,
        GsxResyncState resyncState,
        GroundOpsSignals signals,
        IOptionsMonitor<GsxOptions> options,
        GsxDiagnosticsStore diagnostics,
        ILogger<GsxStartupResyncService> logger)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(groundPrep);
        ArgumentNullException.ThrowIfNull(simVars);
        ArgumentNullException.ThrowIfNull(prosim);
        ArgumentNullException.ThrowIfNull(resyncState);
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(logger);

        _api = api;
        _lifecycle = lifecycle;
        _groundPrep = groundPrep;
        _simVars = simVars;
        _resyncState = resyncState;
        _signals = signals;
        _options = options;
        _diagnostics = diagnostics;
        _logger = logger;

        foreach (var serviceId in TrackedServices)
        {
            _serviceDoneLvars[serviceId] = simVars.Subscribe(
                CompanionLvarNames.ServiceDone(serviceId), "number", DataRefTier.Infrequent);
        }
        _turnaroundLvar = simVars.Subscribe(CompanionLvarNames.Turnaround, "number", DataRefTier.Infrequent);
        _prepDoneLvar = simVars.Subscribe(CompanionLvarNames.PrepDone, "number", DataRefTier.Infrequent);
        _loadsheetPrelimLvar = simVars.Subscribe(CompanionLvarNames.LoadsheetPrelimEdition, "number", DataRefTier.Infrequent);
        _loadsheetFinalLvar = simVars.Subscribe(CompanionLvarNames.LoadsheetFinalSent, "number", DataRefTier.Infrequent);

        _fuelTargetKg = prosim.Subscribe(ProsimDataRefNames.RefuelFuelTargetKg, DataRefTier.Infrequent);
        _fuelTarget = prosim.Subscribe(ProsimDataRefNames.RefuelFuelTarget, DataRefTier.Infrequent);
        _fuelTotal = prosim.Subscribe(ProsimDataRefNames.FuelTotal, DataRefTier.Infrequent);
        _paxBooked = prosim.Subscribe(ProsimDataRefNames.PaxBookedString, DataRefTier.Infrequent);
        _paxOccupied = prosim.Subscribe(ProsimDataRefNames.PaxSeatOccupationString, DataRefTier.Infrequent);
        _efbBoardingStatus = prosim.Subscribe(ProsimDataRefNames.EfbBoardingStatus, DataRefTier.Infrequent);
        _ofpImported = prosim.Subscribe(ProsimDataRefNames.EfbSimbriefPlanImported, DataRefTier.Infrequent);
        _fmsOrigin = prosim.Subscribe(ProsimDataRefNames.FmsOrigin, DataRefTier.Infrequent);
        _fmsDestination = prosim.Subscribe(ProsimDataRefNames.FmsDestination, DataRefTier.Infrequent);

        _lifecycle.ServiceEvent += OnServiceEvent;
        _signals.FlightCycleReset += OnFlightCycleReset;
        _timer = new Timer(_ => Tick(), null, TickInterval, TickInterval);
    }

    public void Dispose()
    {
        _timer.Dispose();
        _lifecycle.ServiceEvent -= OnServiceEvent;
        _signals.FlightCycleReset -= OnFlightCycleReset;
        foreach (var subscription in _serviceDoneLvars.Values)
        {
            subscription.Dispose();
        }
        _turnaroundLvar.Dispose();
        _prepDoneLvar.Dispose();
        _loadsheetPrelimLvar.Dispose();
        _loadsheetFinalLvar.Dispose();
        _fuelTargetKg.Dispose();
        _fuelTarget.Dispose();
        _fuelTotal.Dispose();
        _paxBooked.Dispose();
        _paxOccupied.Dispose();
        _efbBoardingStatus.Dispose();
        _ofpImported.Dispose();
        _fmsOrigin.Dispose();
        _fmsDestination.Dispose();
    }

    private void OnServiceEvent(string serviceId, GsxServiceLifecycleEvent lifecycleEvent)
    {
        if (lifecycleEvent == GsxServiceLifecycleEvent.Completed && !GsxServiceIds.IsToggle(serviceId))
        {
            _ = WriteLvarAsync(CompanionLvarNames.ServiceDone(serviceId), 1);
        }
    }

    /// <summary>The arrival boundary: this sim session is now in a turnaround, and the next
    /// leg's progress tracking starts clean.</summary>
    private void OnFlightCycleReset()
    {
        _prepLvarLatched = false;
        _ = Task.Run(async () =>
        {
            await WriteLvarAsync(CompanionLvarNames.Turnaround, 1).ConfigureAwait(false);
            await WriteLvarAsync(CompanionLvarNames.PrepDone, 0).ConfigureAwait(false);
            await WriteLvarAsync(CompanionLvarNames.LoadsheetPrelimEdition, 0).ConfigureAwait(false);
            await WriteLvarAsync(CompanionLvarNames.LoadsheetFinalSent, 0).ConfigureAwait(false);
            foreach (var serviceId in TrackedServices)
            {
                await WriteLvarAsync(CompanionLvarNames.ServiceDone(serviceId), 0).ConfigureAwait(false);
            }
        });
    }

    private void Tick()
    {
        try
        {
            if (!_resyncState.IsAssessed)
            {
                TryAssess();
                return;
            }

            // Post-assessment upkeep: latch ground-prep completion into its LVAR once.
            if (!_prepLvarLatched && _groundPrep.PrepComplete)
            {
                _prepLvarLatched = true;
                _ = WriteLvarAsync(CompanionLvarNames.PrepDone, 1);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Startup resync tick failed");
        }
    }

    private void TryAssess()
    {
        var timedOut =
            TimeSpan.FromMilliseconds(Environment.TickCount64 - _startedAtTicks) >= AssessmentTimeout;
        var worldKnown = _api.Readiness == GsxReadiness.Ready && _api.Mirror.Services.Count > 0;
        if (!worldKnown)
        {
            if (!timedOut)
            {
                return;
            }

            // GSX never came up — automation cannot run anyway, but the sequencer (and the
            // loadsheet restore) must not wait on us forever.
            RecordDecision("assessment timed out (GSX not ready) — no prior progress assumed");
            _resyncState.MarkAssessed(
                turnaroundDetected: _turnaroundLvar.GetValue(0.0) >= 1,
                loadsheetPrelimEdition: (int)_loadsheetPrelimLvar.GetValue(0.0),
                loadsheetFinalSent: _loadsheetFinalLvar.GetValue(0.0) >= 1);
            return;
        }

        // Give ProSim a chance to report before judging staleness (issue #33) — with no
        // dataref values every check reads "nothing", which must not condemn valid LVARs.
        // RawValue stays null until a subscription's first push.
        var prosimKnown = _fuelTotal.RawValue is not null
            || _fmsOrigin.RawValue is not null
            || _ofpImported.RawValue is not null;
        if (!prosimKnown && !timedOut)
        {
            return;
        }

        var doneLvars = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (serviceId, subscription) in _serviceDoneLvars)
        {
            if (subscription.GetValue(0.0) >= 1)
            {
                doneLvars.Add(serviceId);
            }
        }

        var fuelTarget = _fuelTargetKg.GetValue(0.0);
        if (fuelTarget <= 0)
        {
            fuelTarget = _fuelTarget.GetValue(0.0);
        }

        var evidence = new ResyncEvidence(
            TurnaroundLvar: _turnaroundLvar.GetValue(0.0) >= 1,
            PrepDoneLvar: _prepDoneLvar.GetValue(0.0) >= 1,
            ServiceDoneLvars: doneLvars,
            FuelTargetKg: fuelTarget,
            FuelOnBoardKg: _fuelTotal.GetValue(0.0),
            PaxBooked: SeatMap.Parse(_paxBooked.GetValue<string?>(null)).Count(seat => seat),
            PaxOccupied: SeatMap.Parse(_paxOccupied.GetValue<string?>(null)).Count(seat => seat),
            EfbBoardingStatus: _efbBoardingStatus.GetValue<string?>(null)?.Trim().ToLowerInvariant(),
            LoadsheetPrelimEdition: (int)_loadsheetPrelimLvar.GetValue(0.0),
            LoadsheetFinalSent: _loadsheetFinalLvar.GetValue(0.0) >= 1,
            ConfiguredOneShotServices:
                [.. _options.CurrentValue.DepartureServices
                    .Select(step => step.Service)
                    .Where(id => !string.IsNullOrWhiteSpace(id))],
            FlightPlanLoaded: _ofpImported.GetValue(false)
                || (IsValidIcao(_fmsOrigin.GetValue<string?>(null))
                    && IsValidIcao(_fmsDestination.GetValue<string?>(null))),
            ProsimDataAvailable: prosimKnown);

        var verdict = GsxStartupResync.Assess(evidence);

        if (verdict.StaleTrackingDetected)
        {
            RecordDecision(
                "tracking LVARs claim departure progress the ProSim datarefs contradict "
                + "(ProSim reset?) — cleared; fresh departure flow");
            ClearTrackingLvars();
        }

        foreach (var (serviceId, reason) in verdict.SeedCompleted)
        {
            _lifecycle.SeedCompleted(serviceId);
            RecordDecision($"{serviceId} treated as completed — {reason}");
            // Backfill the tracking LVAR: seeding is event-silent by design, so the
            // completion-edge writer never runs for it. Persisting the verdict makes the NEXT
            // restart resync from the LVARs alone, even after the dataref evidence has moved
            // on (new fuel target, cleared boarding figures).
            _ = WriteLvarAsync(CompanionLvarNames.ServiceDone(serviceId), 1);
        }

        if (verdict.SeedPrepComplete)
        {
            _prepLvarLatched = true;
            _groundPrep.SeedComplete("prep-done tracking LVAR");
            RecordDecision("ground preparation fast-forwarded (already ran this gate session)");
        }

        if (verdict.SeedCompleted.Count == 0 && !verdict.Turnaround)
        {
            RecordDecision("no prior progress detected — fresh departure flow");
        }
        else if (verdict.Turnaround)
        {
            RecordDecision("turnaround leg detected from tracking LVARs");
        }

        _resyncState.MarkAssessed(
            verdict.Turnaround,
            verdict.LoadsheetPrelimEdition,
            verdict.LoadsheetFinalSent,
            verdict.BoardingProven);
    }

    /// <summary>Zeroes every tracking LVAR (stale detection, issue #33) — including the
    /// turnaround and prep flags: a ProSim reset means the user started a fresh flight, and
    /// prep wrote ProSim datarefs (chocks/ground power) the reset also cleared.</summary>
    private void ClearTrackingLvars()
    {
        _prepLvarLatched = false;
        _ = Task.Run(async () =>
        {
            await WriteLvarAsync(CompanionLvarNames.Turnaround, 0).ConfigureAwait(false);
            await WriteLvarAsync(CompanionLvarNames.PrepDone, 0).ConfigureAwait(false);
            await WriteLvarAsync(CompanionLvarNames.LoadsheetPrelimEdition, 0).ConfigureAwait(false);
            await WriteLvarAsync(CompanionLvarNames.LoadsheetFinalSent, 0).ConfigureAwait(false);
            foreach (var serviceId in TrackedServices)
            {
                await WriteLvarAsync(CompanionLvarNames.ServiceDone(serviceId), 0).ConfigureAwait(false);
            }
        });
    }

    /// <summary>Same validity rule as the automation's plan gate: 4 chars, not the MCDU's
    /// "----" placeholder, not ProSim's literal "Null".</summary>
    private static bool IsValidIcao(string? value)
        => value is { Length: 4 }
            && value != "----"
            && !value.Equals("Null", StringComparison.OrdinalIgnoreCase);

    private async Task WriteLvarAsync(string name, double value)
    {
        try
        {
            await _simVars.WriteAsync(name, value).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            // MSFS not connected — tracking degrades silently; the next resync simply has
            // less LVAR evidence to work with.
            _logger.LogDebug("Tracking LVAR write {Name}={Value} skipped: {Reason}", name, value, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tracking LVAR write {Name}={Value} failed", name, value);
        }
    }

    private void RecordDecision(string reason)
    {
        _logger.LogInformation("GSX startup resync: {Reason}", reason);
        _diagnostics.RecordDecision(new GsxDecisionView(DateTimeOffset.UtcNow, "startup resync", reason));
    }
}
