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
/// pair. The nullable facts (<see cref="FuelOnBoardKg"/>, <see cref="PaxOccupied"/>,
/// <see cref="FlightPlanLoaded"/>) are null while their datarefs have not pushed a first
/// value — an unknown fact can neither prove nor CONDEMN (the 2026-08-23 restart judged
/// "ProSim reset?" 5 s after start, before the plan refs had pushed, wiped valid LVARs and
/// re-ran refuelling mid-boarding).</summary>
public sealed record ResyncEvidence(
    bool TurnaroundLvar,
    bool PrepDoneLvar,
    IReadOnlySet<string> ServiceDoneLvars,
    double FuelTargetKg,
    double? FuelOnBoardKg,
    int PaxBooked,
    int? PaxOccupied,
    string? EfbBoardingStatus,
    int LoadsheetPrelimEdition,
    bool LoadsheetFinalSent,
    IReadOnlyList<string> ConfiguredOneShotServices,
    bool? FlightPlanLoaded = true,
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

    /// <summary>The restart fallback for the leg boundary (ADR-0019 §9, issue #220): with no
    /// flight-state snapshot, the tracking LVARs alone can still say "between legs" — an
    /// arrival this sim session, deboarding done, and no departure service of the next leg
    /// seeded (the exempt deboarding / GPU seeds do not count). Anything else means a leg is
    /// under way, or nothing is known, and the machine is left alone.</summary>
    public static bool BetweenLegs(ResyncVerdict verdict, ResyncEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        ArgumentNullException.ThrowIfNull(evidence);
        return verdict.Turnaround
            && evidence.ServiceDoneLvars.Contains(GsxServiceIds.Deboarding)
            && verdict.SeedCompleted.All(seed => ProgressExempt.Contains(seed.ServiceId));
    }

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
            && evidence.FuelOnBoardKg is double fuelAboard
            && fuelAboard < evidence.FuelTargetKg - FuelVarianceKg;
        var boardingContradicted = evidence.ServiceDoneLvars.Contains(GsxServiceIds.Boarding)
            && evidence.PaxOccupied == 0;
        // Never judge staleness on absent data: with ProSim disconnected every dataref reads
        // as "nothing", which must not condemn valid LVARs (degrade, not fail). Null facts
        // (refs not pushed yet) are absent data too — FlightPlanLoaded must read a definite
        // FALSE, not merely not-yet-true, before it may condemn (issue #30 regression
        // 2026-08-23: the 5-seconds-after-start assessment beat the plan refs' first push).
        var stale = evidence.ProsimDataAvailable
            && claimsDeparture
            && (evidence.FlightPlanLoaded == false || refuelContradicted || boardingContradicted);

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
            && evidence.FuelOnBoardKg is double fuelProof
            && fuelProof >= evidence.FuelTargetKg - FuelVarianceKg)
        {
            Prove(
                GsxServiceIds.Refueling,
                $"fuel on board {fuelProof:F0} kg meets the {evidence.FuelTargetKg:F0} kg target");
        }

        if (evidence.PaxBooked > 0
            && evidence.PaxOccupied is int occupied
            && occupied >= evidence.PaxBooked)
        {
            Prove(GsxServiceIds.Boarding, $"{occupied} of {evidence.PaxBooked} booked pax aboard");
        }
        else if (evidence.EfbBoardingStatus is "completed" or "ended" && evidence.PaxOccupied is not 0)
        {
            // An EMPTY cabin under a "completed" EFB status is the previous leg's boarding
            // (EHAM 2026-11-01: restart at the gate after the arrival, before the next plan —
            // the status still read the arrival leg's "completed" and leg two's boarding was
            // never called). The status proves nothing once the passengers have left.
            Prove(GsxServiceIds.Boarding, $"EFB boarding status reads '{evidence.EfbBoardingStatus}'");
        }

        if (prelimEdition > 0)
        {
            // The prelim fires when refuel goes active — it proves the refuel cycle started,
            // and with the fuel target long since reached-or-abandoned we treat it as done.
            Prove(GsxServiceIds.Refueling, $"preliminary loadsheet EDNO {prelimEdition} was already sent");
        }

        var boardingProven = proven.Contains(GsxServiceIds.Boarding);

        // Never re-call: once BOARDING is proven for this leg, the unproven one-shots are
        // assumed done — boarding is the last of them, so everything before it ran. A proven
        // refuel alone proves nothing about catering (EHAM 2026-11-01: the fuel was loaded for
        // leg two, catering and boarding were still to come, and the old rule assumed them
        // done). Deboarding/GPU proofs are exempt — they complete before a fresh leg's
        // departure services, so a restart right after arrival still sequences leg 2 normally.
        if (boardingProven)
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
/// The one-shot startup resync (issue #30) and the forward direction of the tracking LVARs.
/// Forward direction: lifecycle completions, the turnaround boundary and ground-prep
/// completion are written through <see cref="CompanionTrackingLvars"/> (the one writer, which
/// owns the MSFS-down queue) as they happen. Startup direction: once
/// GSX's world is known (Ready + populated mirror), the LVARs and the restart-surviving
/// datarefs are assessed ONCE, proven/assumed services are seeded into the lifecycle tracker,
/// ground prep is fast-forwarded, and <see cref="GsxResyncState"/> is published — the
/// departure sequencer holds until then. The assessment (and its timeout window) waits for
/// the MSFS session proper: the LVARs are created with the flight, so judging them from the
/// main menu would always read "nothing detected". Once in the session, if GSX never comes up
/// the assessment times out and reports "nothing detected" so automation is never deadlocked
/// (degrade, not fail).
/// </summary>
public sealed class GsxStartupResyncService : IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AssessmentTimeout = TimeSpan.FromSeconds(90);

    private readonly IGsxRemoteApi _api;
    private readonly GsxServiceLifecycleTracker _lifecycle;
    private readonly GsxGroundPrepCoordinator _groundPrep;
    private readonly SimSessionStore _simSession;
    private readonly GsxResyncState _resyncState;
    private readonly Core.Flight.ILegBoundaryRecovery? _legBoundary;
    private readonly GroundOpsSignals _signals;
    private readonly CompanionTrackingLvars _tracking;
    private readonly IOptionsMonitor<GsxOptions> _options;
    private readonly GsxDiagnosticsStore _diagnostics;
    private readonly ILogger<GsxStartupResyncService> _logger;

    private readonly Dictionary<string, IDataRefSubscription<double>> _serviceDoneLvars =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly IDataRefSubscription<double> _turnaroundLvar;
    private readonly IDataRefSubscription<double> _prepDoneLvar;
    private readonly IDataRefSubscription<double> _loadsheetPrelimLvar;
    private readonly IDataRefSubscription<double> _loadsheetFinalLvar;
    private readonly IDataRefSubscription<double> _fuelTargetKg;
    private readonly IDataRefSubscription<double> _fuelTarget;
    private readonly IDataRefSubscription<double> _fuelTotal;
    private readonly IDataRefSubscription<string?> _paxBooked;
    private readonly IDataRefSubscription<string?> _paxOccupied;
    private readonly IDataRefSubscription<string?> _efbBoardingStatus;
    private readonly IDataRefSubscription<bool> _ofpImported;
    private readonly IDataRefSubscription<string?> _fmsOrigin;
    private readonly IDataRefSubscription<string?> _fmsDestination;

    private readonly Timer _timer;
    private readonly SessionWindow _assessWindow;
    private bool _prepLvarLatched;
    private bool _loggedSessionWait;

    public GsxStartupResyncService(
        IGsxRemoteApi api,
        GsxServiceLifecycleTracker lifecycle,
        GsxGroundPrepCoordinator groundPrep,
        ISimVars simVars,
        SimSessionStore simSession,
        IProsimDataRefs prosim,
        GsxResyncState resyncState,
        GroundOpsSignals signals,
        CompanionTrackingLvars tracking,
        IOptionsMonitor<GsxOptions> options,
        GsxDiagnosticsStore diagnostics,
        ILogger<GsxStartupResyncService> logger,
        Core.Flight.ILegBoundaryRecovery? legBoundary = null)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(groundPrep);
        ArgumentNullException.ThrowIfNull(simVars);
        ArgumentNullException.ThrowIfNull(simSession);
        ArgumentNullException.ThrowIfNull(prosim);
        ArgumentNullException.ThrowIfNull(resyncState);
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(tracking);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(logger);

        _api = api;
        _lifecycle = lifecycle;
        _groundPrep = groundPrep;
        _simSession = simSession;
        _resyncState = resyncState;
        _signals = signals;
        _tracking = tracking;
        _options = options;
        _diagnostics = diagnostics;
        _logger = logger;
        _legBoundary = legBoundary;

        foreach (var serviceId in CompanionTrackingLvars.TrackedServices)
        {
            _serviceDoneLvars[serviceId] = simVars.Subscribe(CompanionLvarNames.ServiceDone(serviceId));
        }
        _turnaroundLvar = simVars.Subscribe(CompanionLvarNames.Turnaround);
        _prepDoneLvar = simVars.Subscribe(CompanionLvarNames.PrepDone);
        _loadsheetPrelimLvar = simVars.Subscribe(CompanionLvarNames.LoadsheetPrelimEdition);
        _loadsheetFinalLvar = simVars.Subscribe(CompanionLvarNames.LoadsheetFinalSent);

        _fuelTargetKg = prosim.Subscribe(ProsimDataRefNames.RefuelFuelTargetKg);
        _fuelTarget = prosim.Subscribe(ProsimDataRefNames.RefuelFuelTarget);
        _fuelTotal = prosim.Subscribe(ProsimDataRefNames.FuelTotal);
        _paxBooked = prosim.Subscribe(ProsimDataRefNames.PaxBookedString);
        _paxOccupied = prosim.Subscribe(ProsimDataRefNames.PaxSeatOccupationString);
        _efbBoardingStatus = prosim.Subscribe(ProsimDataRefNames.EfbBoardingStatus);
        _ofpImported = prosim.Subscribe(ProsimDataRefNames.EfbSimbriefPlanImported);
        _fmsOrigin = prosim.Subscribe(ProsimDataRefNames.FmsOrigin);
        _fmsDestination = prosim.Subscribe(ProsimDataRefNames.FmsDestination);

        _lifecycle.ServiceEvent += OnServiceEvent;
        _signals.FlightCycleReset += OnFlightCycleReset;
        _simSession.SessionEnded += OnSessionEnded;
        _assessWindow = simSession.OpenWindow(AssessmentTimeout);
        _timer = new Timer(_ => Tick(), null, TickInterval, TickInterval);
    }

    public void Dispose()
    {
        _timer.Dispose();
        _lifecycle.ServiceEvent -= OnServiceEvent;
        _signals.FlightCycleReset -= OnFlightCycleReset;
        _simSession.SessionEnded -= OnSessionEnded;
        _assessWindow.Dispose();
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
            _ = _tracking.MarkServiceDoneAsync(serviceId);
        }
    }

    /// <summary>The pilot left the flight session: the tracking LVARs died with it, and the
    /// ground-prep chain will re-run in the next session — re-arm the prep-done latch so that
    /// completion is written to the fresh session's LVAR (otherwise an app restart in the new
    /// session would find PrepDone=0 and re-drive the reposition mid-turnaround).</summary>
    private void OnSessionEnded() => _prepLvarLatched = false;

    /// <summary>The arrival boundary: this sim session is now in a turnaround, and the next
    /// leg's progress tracking starts clean.</summary>
    private void OnFlightCycleReset()
    {
        if (_signals.LastFlightCycleResetKind == FlightCycleResetKind.NewPlan)
        {
            // A different plan at the stand (ADR-0019 §6, issue #213) is the departure's
            // business: the turnaround LVAR already reads 1, PrepDone must stay 1 (an app
            // restart after the reset would otherwise re-drive the reposition under the
            // passengers) and the departure one-shots are cleared by the automation itself.
            return;
        }

        _prepLvarLatched = false;
        // The verdict is one-shot, but the arrival is not (issue #168): an app started in
        // flight assessed "no turnaround" at cruise; the next leg's cold-and-dark check must
        // still see a turnaround, not a fresh start.
        _resyncState.MarkTurnaround();
        _ = _tracking.ResetForArrivalAsync();
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
                _ = _tracking.MarkPrepDoneAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Startup resync tick failed");
        }
    }

    private void TryAssess()
    {
        // The evidence lives in the sim session: the tracking LVARs are created with the
        // flight and vanish with it, and ProSim's datarefs settle only once the pilot is
        // aboard. Assessing from the main menu would always conclude "nothing detected" and
        // latch it, so the assessment (and its 90 s session window) waits for session entry.
        // Walkaround counts — DataIsMeaningful — because the LVARs exist there.
        if (!_simSession.Snapshot().DataIsMeaningful)
        {
            if (!_loggedSessionWait)
            {
                _loggedSessionWait = true;
                RecordDecision("waiting for the MSFS session before assessing (automation holds)");
            }
            return;
        }

        _loggedSessionWait = false;
        var timedOut = _assessWindow.Elapsed;
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
                turnaroundDetected: _turnaroundLvar.Value >= 1,
                loadsheetPrelimEdition: (int)_loadsheetPrelimLvar.Value,
                loadsheetFinalSent: _loadsheetFinalLvar.Value >= 1);
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
            if (subscription.Value >= 1)
            {
                doneLvars.Add(serviceId);
            }
        }

        // The staleness judgement compares the tracking LVARs against the plan refs — so
        // when the LVARs claim progress, WAIT for those refs' first push (issue #30
        // regression 2026-08-23: assessed 5 s after start, before efb.simbriefPlanImported
        // / FMS origin had pushed, condemned valid LVARs as "ProSim reset?" and re-ran
        // refuelling + catering mid-boarding). The 90 s window still bounds the wait.
        var planKnown = _ofpImported.RawValue is not null
            || (_fmsOrigin.RawValue is not null && _fmsDestination.RawValue is not null);
        var lvarsClaimProgress = doneLvars.Count > 0 || (int)_loadsheetPrelimLvar.Value > 0;
        if (lvarsClaimProgress && !planKnown && !timedOut)
        {
            return;
        }

        var fuelTarget = _fuelTargetKg.Value;
        if (fuelTarget <= 0)
        {
            fuelTarget = _fuelTarget.Value;
        }

        var evidence = new ResyncEvidence(
            TurnaroundLvar: _turnaroundLvar.Value >= 1,
            PrepDoneLvar: _prepDoneLvar.Value >= 1,
            ServiceDoneLvars: doneLvars,
            FuelTargetKg: fuelTarget,
            FuelOnBoardKg: _fuelTotal.RawValue is null ? null : _fuelTotal.Value,
            PaxBooked: SeatMap.Parse(_paxBooked.Value).Count(seat => seat),
            PaxOccupied: _paxOccupied.RawValue is null
                ? null
                : SeatMap.Parse(_paxOccupied.Value).Count(seat => seat),
            EfbBoardingStatus: _efbBoardingStatus.Value?.Trim().ToLowerInvariant(),
            LoadsheetPrelimEdition: (int)_loadsheetPrelimLvar.Value,
            LoadsheetFinalSent: _loadsheetFinalLvar.Value >= 1,
            ConfiguredOneShotServices:
                [.. _options.CurrentValue.DepartureServices
                    .Select(step => step.Service)
                    .Where(id => !string.IsNullOrWhiteSpace(id))],
            FlightPlanLoaded: planKnown
                ? _ofpImported.Value
                    || (IsValidIcao(_fmsOrigin.Value)
                        && IsValidIcao(_fmsDestination.Value))
                : null,
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
            _ = _tracking.MarkServiceDoneAsync(serviceId);
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

        // ADR-0019 §9 (issue #220): the leg-boundary machine's fallback when no snapshot came
        // back — it adopts the step only while it knows nothing itself.
        if (_legBoundary is not null && GsxStartupResync.BetweenLegs(verdict, evidence)
            && _legBoundary.AdoptDeboardedFromEvidence("startup resync: tracking LVARs"))
        {
            RecordDecision("leg boundary: Turnaround adopted from the tracking LVARs (deboarding done, no departure progress, no snapshot)");
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
        _ = _tracking.ClearAllAsync();
    }

    /// <summary>Same validity rule as the automation's plan gate: 4 chars, not the MCDU's
    /// "----" placeholder, not ProSim's literal "Null".</summary>
    private static bool IsValidIcao(string? value)
        => value is { Length: 4 }
            && value != "----"
            && !value.Equals("Null", StringComparison.OrdinalIgnoreCase);

    private void RecordDecision(string reason)
    {
        _logger.LogInformation("GSX startup resync: {Reason}", reason);
        _diagnostics.RecordDecision(new GsxDecisionView(DateTimeOffset.UtcNow, "startup resync", reason));
    }
}
