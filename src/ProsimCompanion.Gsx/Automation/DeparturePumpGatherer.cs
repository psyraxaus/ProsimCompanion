using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;
using ProsimCompanion.Gsx.Services;

namespace ProsimCompanion.Gsx.Automation;

/// <summary>
/// Everything one pump evaluation read, in one record: the pure core's inputs plus the few
/// figures the effect planner needs that the core does not (the configured step list for the
/// waiting board, whether the de-icing store already knows the step is next, the new
/// plan-availability stamp, the FOB for the tankering-reversal line, the turnaround settle
/// parameters for the decision text).
/// </summary>
internal sealed record DeparturePumpReading(
    DepartureAutomationCore.PumpInputs Inputs,
    IReadOnlyList<DepartureServiceStep> ConfiguredSteps,
    bool DeiceStepNext,
    DateTimeOffset? PlanAvailableSinceUtc,
    double FuelOnBoardKg,
    int TurnaroundAutoStartDelaySeconds,
    bool TurnaroundAskFirst,
    DateTimeOffset Now);

/// <summary>
/// The departure pump's inputs gatherer: the one module that reads the live collaborators
/// (options, GSX mirror, trigger slot, lifecycle cycles, flight-plan status, OFP store,
/// de-icing and fuel-confirmation stores, the fuel datarefs) into the pure core's
/// <see cref="DepartureAutomationCore.PumpInputs"/>. It performs no effects and keeps no
/// cycle state of its own — the latches arrive as a snapshot and the one latch it derives
/// (the plan-availability stamp) goes back in the reading for the planner to store. Every
/// collaborator is an interface or an in-memory store, so a test fakes them one by one.
/// </summary>
public sealed class DeparturePumpGatherer : IDisposable
{
    /// <summary>EFB planned-fuel settle window (#118, 2026-09-05): with no OFP yet, the
    /// tankering decision trusts <c>efb.plannedfuel</c> only after the plan has stood this
    /// long — the import the plan detection itself starts needs time to overwrite the
    /// previous leg's figure (imports land in seconds; failures leave the pilot's own EFB
    /// entry as the honest fallback).</summary>
    internal static readonly TimeSpan TankeringPlanSettle = TimeSpan.FromSeconds(90);

    /// <summary>Hold reason for the Refueling step under <c>gsx.refuelCall = onFuelConfirmed</c>
    /// (2026-09-19). Probed by <c>refuel-on-fuel-confirmed-hold</c> — keep the wording stable.</summary>
    internal const string FuelConfirmationHoldReason =
        "waiting for the crew to confirm the block fuel (INIT page CONFIRM FUEL, "
        + "'fuel confirmed' by voice, or 'request refueling') — other services continue";

    private readonly IGsxRemoteApi _api;
    private readonly IGsxTriggerSlot _slot;
    private readonly IGsxServiceLifecycleView _lifecycle;
    private readonly DepartureCycleState _cycle;
    private readonly IGsxFlightPlanStatus _flightPlan;
    private readonly OfpStore _ofpStore;
    private readonly GsxResyncState _resyncState;
    private readonly FuelConfirmationStore _fuelConfirmation;
    private readonly IEfbInitOverrides _initOverrides;
    private readonly DeiceRequestStore _deiceRequest;
    private readonly IOptionsMonitor<GsxOptions> _options;

    // Tankering pre-skip inputs (#117): cached reads only, same refs the refuel sync latches.
    private readonly IDataRefSubscription<double> _fuelTotal;
    private readonly IDataRefSubscription<double> _plannedFuel;

    public DeparturePumpGatherer(
        IGsxRemoteApi api,
        IGsxTriggerSlot slot,
        IGsxServiceLifecycleView lifecycle,
        DepartureCycleState cycle,
        IGsxFlightPlanStatus flightPlan,
        OfpStore ofpStore,
        GsxResyncState resyncState,
        FuelConfirmationStore fuelConfirmation,
        IEfbInitOverrides initOverrides,
        DeiceRequestStore deiceRequest,
        IOptionsMonitor<GsxOptions> options,
        IProsimDataRefs prosim)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(cycle);
        ArgumentNullException.ThrowIfNull(flightPlan);
        ArgumentNullException.ThrowIfNull(ofpStore);
        ArgumentNullException.ThrowIfNull(resyncState);
        ArgumentNullException.ThrowIfNull(fuelConfirmation);
        ArgumentNullException.ThrowIfNull(initOverrides);
        ArgumentNullException.ThrowIfNull(deiceRequest);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(prosim);

        _api = api;
        _slot = slot;
        _lifecycle = lifecycle;
        _cycle = cycle;
        _flightPlan = flightPlan;
        _ofpStore = ofpStore;
        _resyncState = resyncState;
        _fuelConfirmation = fuelConfirmation;
        _initOverrides = initOverrides;
        _deiceRequest = deiceRequest;
        _options = options;
        _fuelTotal = prosim.Subscribe(ProsimDataRefNames.FuelTotal);
        _plannedFuel = prosim.Subscribe(ProsimDataRefNames.EfbPlannedFuel);
    }

    public void Dispose()
    {
        _fuelTotal.Dispose();
        _plannedFuel.Dispose();
    }

    /// <summary>Reads every input for one evaluation at <paramref name="now"/>.</summary>
    /// <param name="phase">The host's current automation phase.</param>
    /// <param name="latches">The cycle latches as the host holds them right now.</param>
    /// <param name="now">The evaluation instant (UTC).</param>
    internal DeparturePumpReading Read(GsxAutomationPhase phase, DepartureLatchSnapshot latches, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(latches);
        var options = _options.CurrentValue;

        // Plan-figure settling for the tankering decision (#118): stamp when the plan
        // first appears; the EFB fallback figure is trusted once the OFP is imported or
        // the settle window has passed with no import landing.
        var flightPlanAvailable = _flightPlan.FlightPlanAvailable;
        var planAvailableSince = flightPlanAvailable ? latches.PlanAvailableSinceUtc ?? now : (DateTimeOffset?)null;
        var planFiguresSettled = _flightPlan.OfpImported
            || (planAvailableSince is { } since && now - since >= TankeringPlanSettle);

        var cycles = _lifecycle.SnapshotCycles();
        DepartureCycleView Cycle(string id)
        {
            cycles.TryGetValue(id, out var c);
            return new DepartureCycleView(
                Called: c.Called,
                ReachedRequested: c.Requested || c.Active || c.Completed,
                ReachedActive: c.Active || c.Completed,
                Completed: c.Completed);
        }

        // The de-icing policy (2026-10-09) shapes the step list for THIS cycle: a requested
        // de-ice wakes a skipped DeIce step (or appends one last); an open captain's
        // question keeps the step on the board, held; a declined one skips it.
        var deice = _deiceRequest.Snapshot();
        var steps = DeiceRequestPolicy.EffectiveSteps(options.DepartureServices, deice, now);

        var ofp = _ofpStore.Current;
        var fuelOnBoard = _fuelTotal.Value;
        var plannedFuelRaw = _plannedFuel.Value;
        var skipOnTankering = options.SkipRefuelOnTankering;
        // The crew's INIT FUEL RAMP override is the plan when set (2026-09-19).
        var planKg = EffectiveBlockFuel.PlanKg(_initOverrides.Snapshot(), ofp);
        var refuelHeldForConfirmation = GsxOptions.IsRefuelOnFuelConfirmed(options.RefuelCall)
            && !_fuelConfirmation.Confirmed;

        var inputs = new DepartureAutomationCore.PumpInputs(
            AutomationEnabled: options.AutomationEnabled,
            GsxReady: _api.Readiness == GsxReadiness.Ready,
            ResyncAssessed: _resyncState.IsAssessed,
            AutoStartOption: options.AutoStartDepartureServices,
            Phase: phase,
            CycleStarted: _cycle.Started,
            CycleComplete: _cycle.Complete,
            PrepComplete: _cycle.PrepComplete,
            RequireOfp: options.RequireOfpBeforeDeparture,
            FmsPlanPresent: _flightPlan.FmsPlanPresent,
            OfpImported: _flightPlan.OfpImported,
            FlightPlanAvailable: flightPlanAvailable,
            FmsOrigin: _flightPlan.FmsOrigin,
            FmsDestination: _flightPlan.FmsDestination,
            Forced: latches.ForceNext,
            InFlightServiceId: _slot.InFlightServiceId,
            IsTurnaround: _cycle.IsTurnaround,
            IsCompanyHub: IsCompanyHub(_api.Mirror.AirportIcao, options.CompanyHubs),
            EstimatedEnroute: ofp?.EstimatedEnroute,
            Steps: steps,
            MirrorServices: _api.Mirror.Services,
            Cycle: Cycle,
            PreSkip: id => id.Equals(GsxServiceIds.Refueling, StringComparison.OrdinalIgnoreCase)
                ? Sync.RefuelCore.TankeringSkipReason(
                    skipOnTankering,
                    flightPlanAvailable,
                    planFiguresSettled,
                    fuelOnBoard,
                    planKg,
                    plannedFuelRaw)
                : id.Equals(GsxServiceIds.DeIce, StringComparison.OrdinalIgnoreCase)
                    ? DeiceRequestPolicy.SkipReason(deice)
                    : null,
            VoiceActivationMode: Sync.GsxGroundPrepCoordinator.IsVoiceActivation(options.GroundPrepActivation),
            // Real-world SOP (2026-09-19): with gsx.refuelCall = onFuelConfirmed the truck
            // waits for the crew's block-fuel confirmation; every other service proceeds.
            // The DeIce step waits the same way for the captain's de-icing answer.
            PreHold: id => id.Equals(GsxServiceIds.Refueling, StringComparison.OrdinalIgnoreCase)
                && refuelHeldForConfirmation
                ? FuelConfirmationHoldReason
                : id.Equals(GsxServiceIds.DeIce, StringComparison.OrdinalIgnoreCase)
                    ? DeiceRequestPolicy.HoldReason(deice, now)
                    : null,
            Turnaround: new DepartureAutomationCore.TurnaroundStartInputs(
                Enabled: GsxOptions.IsVoiceThenAuto(options.GroundPrepActivation),
                NextLegOfpLoaded: NextLegOfpLoaded(latches.ArrivalOfpKnown, latches.ArrivalOfp, ofp),
                ReadySinceUtc: latches.TurnaroundReadySinceUtc,
                Now: now,
                Delay: TimeSpan.FromSeconds(Math.Max(0, options.TurnaroundAutoStartDelaySeconds)),
                AskFirst: options.TurnaroundAskFirst,
                QuestionAsked: latches.TurnaroundQuestionAsked,
                Declined: latches.TurnaroundDeclined));

        return new DeparturePumpReading(
            Inputs: inputs,
            ConfiguredSteps: options.DepartureServices,
            DeiceStepNext: deice.StepNext,
            PlanAvailableSinceUtc: planAvailableSince,
            FuelOnBoardKg: fuelOnBoard,
            TurnaroundAutoStartDelaySeconds: Math.Max(0, options.TurnaroundAutoStartDelaySeconds),
            TurnaroundAskFirst: options.TurnaroundAskFirst,
            Now: now);
    }

    /// <summary>True once the OFP store holds a plan other than the one the aircraft arrived
    /// with (issue #169). Before any arrival this session nothing counts — the first leg stays
    /// on the voice gate by design.</summary>
    internal static bool NextLegOfpLoaded(bool arrivalOfpKnown, OfpData? arrivalOfp, OfpData? current)
    {
        if (!arrivalOfpKnown || current is null)
        {
            return false;
        }

        if (arrivalOfp is null)
        {
            return true;
        }

        if (ReferenceEquals(current, arrivalOfp))
        {
            return false;
        }

        // Same SimBrief request id = the same plan re-imported (or re-fetched), not a new leg.
        return current.RequestId.Length == 0
            || !string.Equals(current.RequestId, arrivalOfp.RequestId, StringComparison.Ordinal);
    }

    /// <summary>Whether the currently loaded airport matches a configured company-hub ICAO
    /// prefix (case-insensitive). Unknown airport ⇒ non-hub, so hub-only services simply hold
    /// off rather than firing at an unidentified field.</summary>
    internal static bool IsCompanyHub(string? icao, IReadOnlyList<string> companyHubs)
    {
        ArgumentNullException.ThrowIfNull(companyHubs);
        if (string.IsNullOrWhiteSpace(icao) || companyHubs.Count == 0)
        {
            return false;
        }
        return companyHubs.Any(prefix =>
            !string.IsNullOrWhiteSpace(prefix)
            && icao.StartsWith(prefix.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}
