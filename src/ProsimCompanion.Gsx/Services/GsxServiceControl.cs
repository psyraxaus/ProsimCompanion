using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Gsx.Automation;
using ProsimCompanion.Gsx.Mirror;
using ProsimCompanion.Gsx.Protocol;
using ProsimCompanion.Gsx.Sync;

namespace ProsimCompanion.Gsx.Services;

/// <summary>
/// Implements the Core <see cref="IGsxServiceControl"/> seam: pre-flight checks against the
/// state mirror + lifecycle cycles (never raw ordinals, never the menu), then dispatch through
/// the shared <see cref="IGsxTriggerSlot"/> — the SAME serialized service.trigger path every
/// sender uses, so a user command can never rapid-fire GSX alongside the automation.
///
/// By-design refusals (documented in docs/integrations/command-api.md):
/// - <b>Pushback</b> is beacon-orchestrated (<see cref="GsxPushbackSequenceService"/>): doors
///   close, jetway retracts and equipment clears on randomized crew delays before the call
///   goes out. A direct trigger would race that sequence (or push with the jetway attached),
///   so the command always answers NotCallable with the supported flow.
/// - <b>Jetway/stairs connect</b> while ground preparation is still running: the prep
///   coordinator owns that step (second-writer risk), so the command yields to it.
/// </summary>
public sealed class GsxServiceControl : IGsxServiceControl, IDisposable
{
    private const string JetwayServiceId = GsxServiceIds.OperateJetways;
    private const string StairsServiceId = GsxServiceIds.OperateStairs;

    /// <summary>L:FSDT_GSX_JETWAY value meaning "no jetway exists at this position" — see the
    /// 2026-08-02 live-session archaeology in <see cref="GsxJetwayStairsService"/>.</summary>
    private const int JetwayLvarNotPresent = 2;

    private readonly IGsxRemoteApi _api;
    private readonly GsxServiceLifecycleTracker _lifecycle;
    private readonly IGsxTriggerSlot _slot;
    private readonly IGsxGroundPrepStatus _groundPrep;
    private readonly IFlightPhaseSource _flightPhase;
    private readonly IGsxFlightPlanStatus _flightPlan;
    private readonly SimSessionStore _simSession;
    private readonly IOptionsMonitor<GsxOptions> _options;
    private readonly FuelConfirmationStore _fuelConfirmation;
    private readonly IEfbInitOverrides _initOverrides;
    private readonly OfpStore _ofpStore;
    private readonly GsxDiagnosticsStore _diagnostics;
    private readonly ILogger<GsxServiceControl> _logger;
    private readonly IDataRefSubscription<double> _jetwayLvar;
    private readonly IDataRefSubscription<double> _fuelTotal;
    private readonly IDataRefSubscription<double> _plannedFuel;

    public GsxServiceControl(
        IGsxRemoteApi api,
        GsxServiceLifecycleTracker lifecycle,
        IGsxTriggerSlot slot,
        IGsxGroundPrepStatus groundPrep,
        IFlightPhaseSource flightPhase,
        IGsxFlightPlanStatus flightPlan,
        SimSessionStore simSession,
        ISimVars simVars,
        IProsimDataRefs prosim,
        FuelConfirmationStore fuelConfirmation,
        IEfbInitOverrides initOverrides,
        OfpStore ofpStore,
        GsxDiagnosticsStore diagnostics,
        IOptionsMonitor<GsxOptions> options,
        ILogger<GsxServiceControl> logger)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(groundPrep);
        ArgumentNullException.ThrowIfNull(flightPhase);
        ArgumentNullException.ThrowIfNull(flightPlan);
        ArgumentNullException.ThrowIfNull(simSession);
        ArgumentNullException.ThrowIfNull(simVars);
        ArgumentNullException.ThrowIfNull(prosim);
        ArgumentNullException.ThrowIfNull(fuelConfirmation);
        ArgumentNullException.ThrowIfNull(initOverrides);
        ArgumentNullException.ThrowIfNull(ofpStore);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _api = api;
        _lifecycle = lifecycle;
        _slot = slot;
        _groundPrep = groundPrep;
        _flightPhase = flightPhase;
        _flightPlan = flightPlan;
        _simSession = simSession;
        _fuelConfirmation = fuelConfirmation;
        _initOverrides = initOverrides;
        _ofpStore = ofpStore;
        _diagnostics = diagnostics;
        _options = options;
        _logger = logger;

        _jetwayLvar = simVars.Subscribe(GsxLvarNames.Jetway);
        _fuelTotal = prosim.Subscribe(ProsimDataRefNames.FuelTotal);
        _plannedFuel = prosim.Subscribe(ProsimDataRefNames.EfbPlannedFuel);
    }

    public void Dispose()
    {
        _jetwayLvar.Dispose();
        _fuelTotal.Dispose();
        _plannedFuel.Dispose();
    }

    public async Task<GsxServiceCallOutcome> TryCallAsync(
        GsxServiceAction action,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await TryCallCoreAsync(action, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Service call {Action} failed", action);
            return new(GsxServiceCallStatus.Rejected, $"The call failed unexpectedly: {ex.Message}");
        }
    }

    private async Task<GsxServiceCallOutcome> TryCallCoreAsync(
        GsxServiceAction action,
        CancellationToken cancellationToken)
    {
        // Pushback is beacon-orchestrated by design — never triggered directly (it would race
        // the door/jetway/equipment sequence, or push with the jetway still attached).
        if (action == GsxServiceAction.RequestPushback)
        {
            return new(
                GsxServiceCallStatus.NotCallable,
                _options.CurrentValue.BeaconPushbackSequenceEnabled
                    ? "Pushback is beacon-orchestrated: complete the departure services and switch "
                      + "the beacon on — the sequence closes doors, retracts the jetway, clears "
                      + "ground equipment, then calls pushback."
                    : "The beacon pushback sequence is disabled (gsx.beaconPushbackSequenceEnabled) "
                      + "— request pushback from the GSX menu.");
        }

        // Session gate (ADR-0006 / issue #50): the automatic pillars already hold outside the
        // MSFS session; the on-demand path must not slip past them. Unknown (SimConnect
        // absent) deliberately does NOT block — degrade, not fail.
        switch (_simSession.Phase)
        {
            case SimSessionPhase.NotInSession:
                return new(
                    GsxServiceCallStatus.Unavailable,
                    "MSFS is not in a flight session — load into the flight deck first.");
            case SimSessionPhase.Walkaround:
                return new(
                    GsxServiceCallStatus.NotCallable,
                    "You are on the walkaround — ground services wait for the flight deck.");
        }

        switch (_api.Readiness)
        {
            case GsxReadiness.Disconnected:
                return new(GsxServiceCallStatus.Unavailable, "GSX is not connected.");
            case GsxReadiness.ConnectedGsxNotRunning:
                return new(GsxServiceCallStatus.Unavailable, "GSX is not running.");
        }

        return action switch
        {
            GsxServiceAction.RetractJetway => await RetractAsync(JetwayServiceId, "jetway", cancellationToken).ConfigureAwait(false),
            GsxServiceAction.RetractStairs => await RetractAsync(StairsServiceId, "stairs", cancellationToken).ConfigureAwait(false),
            GsxServiceAction.ConfirmFuel => await ConfirmFuelAsync(cancellationToken).ConfigureAwait(false),
            _ => await RequestAsync(action, cancellationToken).ConfigureAwait(false),
        };
    }

    /// <summary>
    /// The crew confirms the block fuel (2026-09-19, real-world SOP): the confirmation is
    /// recorded FIRST — it releases the sequencer's <c>onFuelConfirmed</c> hold even when the
    /// truck cannot be ordered right now (plan gate, slot busy) — and then the refuel is
    /// requested through the normal path, which also covers the top-up after a completed
    /// refuel. The figure is the INIT FUEL RAMP override, else the OFP, else the EFB planned
    /// fuel (<see cref="EffectiveBlockFuel"/>).
    /// </summary>
    private async Task<GsxServiceCallOutcome> ConfirmFuelAsync(CancellationToken cancellationToken)
    {
        var figure = EffectiveFigure();
        if (!figure.HasValue)
        {
            return new(
                GsxServiceCallStatus.NotCallable,
                "No block-fuel figure to confirm yet — import the SimBrief OFP or enter FUEL RAMP on the INIT page.");
        }

        var reconfirmed = _fuelConfirmation.Confirmed;
        _fuelConfirmation.Confirm(figure.Kg, "confirm");
        RecordDecision(
            "fuel confirmed",
            $"{figure.Kg:F0} kg ({SourceLabel(figure.Source)}) confirmed by the crew{(reconfirmed ? " (re-confirmed)" : "")}");

        var outcome = await RequestAsync(GsxServiceAction.RequestRefuel, cancellationToken).ConfigureAwait(false);
        var prefix = $"Fuel figure {figure.Kg:F0} kg confirmed";
        return outcome.Status switch
        {
            GsxServiceCallStatus.Called => new(GsxServiceCallStatus.Called, $"{prefix} — refueling requested."),
            GsxServiceCallStatus.AlreadySatisfied => new(GsxServiceCallStatus.AlreadySatisfied, $"{prefix} — {Decapitalize(outcome.Detail)}"),
            // NotCallable / Unavailable / Rejected: the confirmation stands — the departure
            // sequence orders the truck itself once the gate clears.
            _ => outcome with { Detail = $"{prefix}. {outcome.Detail}" },
        };
    }

    private BlockFuelFigure EffectiveFigure()
        => EffectiveBlockFuel.Resolve(_initOverrides.Snapshot(), _ofpStore.Current, _plannedFuel.Value);

    private static string SourceLabel(BlockFuelSource source) => source switch
    {
        BlockFuelSource.Override => "INIT override",
        BlockFuelSource.Ofp => "OFP block fuel",
        BlockFuelSource.EfbPlannedFuel => "EFB planned fuel",
        _ => "no figure",
    };

    private void RecordDecision(string action, string reason)
    {
        _logger.LogInformation("GSX {Action}: {Reason}", action, reason);
        _diagnostics.RecordDecision(new GsxDecisionView(DateTimeOffset.UtcNow, action, reason));
    }

    private async Task<GsxServiceCallOutcome> RequestAsync(
        GsxServiceAction action,
        CancellationToken cancellationToken)
    {
        var serviceId = ServiceIdFor(action);
        var display = DisplayNameFor(action);

        // Flight-plan gate (ADR-0006 / issue #50): the sequencer has always held these until a
        // plan exists; the on-demand path (voice, web, API) now applies the same rule instead
        // of going straight to GSX. Every pre-takeoff ground phase is gated (issue #60: the
        // old Preflight/ColdAndDark-only gate let plan-less requests through in other ground
        // phases) — an arrival deboarding or a turnaround already in progress never is.
        if (action is GsxServiceAction.RequestRefuel or GsxServiceAction.RequestCatering or GsxServiceAction.RequestBoarding
            && _options.CurrentValue.RequireOfpBeforeDeparture
            && (_flightPhase.CurrentPhase.IsAtGate()
                || _flightPhase.CurrentPhase is FlightPhase.PushbackAndStart or FlightPhase.TaxiOut)
            && !_flightPlan.FlightPlanAvailable)
        {
            return new(
                GsxServiceCallStatus.NotCallable,
                "No flight plan yet — import the SimBrief OFP or load the plan in the MCDU "
                + $"before calling {display} (gsx.requireOfpBeforeDeparture).");
        }

        if (action is GsxServiceAction.RequestJetway or GsxServiceAction.RequestStairs)
        {
            if (action == GsxServiceAction.RequestJetway
                && (int)_jetwayLvar.Value == JetwayLvarNotPresent)
            {
                // GSX 4 lists (and acks!) OperateJetways at jetway-less stands — the LVAR is
                // the truth. Refuse instead of "succeeding" invisibly.
                return new(
                    GsxServiceCallStatus.NotCallable,
                    "There is no jetway at this stand (GSX jetway LVAR reads 2) — try gsx.requestStairs.");
            }

            // Ground prep owns the connect step while it is running — yielding avoids a second
            // writer racing the coordinator's own trigger/verify cycle.
            if (GroundPrepOwnsJetwayStairs())
            {
                return new(
                    GsxServiceCallStatus.NotCallable,
                    "Ground preparation is still running — the jetway/stairs are connected "
                    + "automatically once it reaches that step.");
            }
        }

        var service = _api.Mirror.Services.GetValueOrDefault(serviceId);
        if (service is null)
        {
            return new(
                GsxServiceCallStatus.NotCallable,
                $"GSX does not offer {display} in the current gate context.");
        }

        // Fuel top-up (2026-09-19, real-world SOP): the crew raised the block fuel AFTER the
        // truck completed. A completed Refueling is not "already satisfied" when the fuel on
        // board is short of the current figure — the cycle is re-armed and the truck ordered
        // again. GSX allows a second refuel; the refuel sync latches the new target on the
        // new Active edge.
        if (action == GsxServiceAction.RequestRefuel
            && !_lifecycle.IsPending(serviceId)
            && (service.State == GsxServiceState.Completed || _lifecycle.IsCompleted(serviceId)))
        {
            return await RequestRefuelTopUpAsync(service, display, cancellationToken).ConfigureAwait(false);
        }

        // The jetway/stairs trigger is a TOGGLE: Active/Completed means connected — never
        // re-fire (that would retract it). For plain services those states mean the request
        // already holds. Either way: AlreadySatisfied.
        switch (service.State)
        {
            case GsxServiceState.Requested:
                return new(GsxServiceCallStatus.AlreadySatisfied, $"{display} is already requested — crew en route.");
            case GsxServiceState.Active:
                return IsToggle(action)
                    ? new(GsxServiceCallStatus.AlreadySatisfied, $"The {display} is already connected.")
                    : new(GsxServiceCallStatus.AlreadySatisfied, $"{display} is already in progress.");
            case GsxServiceState.Completed:
                return IsToggle(action)
                    ? new(GsxServiceCallStatus.AlreadySatisfied, $"The {display} is already connected.")
                    : new(GsxServiceCallStatus.AlreadySatisfied, $"{display} has already completed this turnaround.");
            case GsxServiceState.Bypassed:
                return new(GsxServiceCallStatus.NotCallable, $"GSX has bypassed {display} this turnaround.");
        }

        if (_lifecycle.IsPending(serviceId))
        {
            return new(GsxServiceCallStatus.AlreadySatisfied, $"{display} has already been called — awaiting GSX.");
        }

        if (!IsToggle(action) && _lifecycle.IsCompleted(serviceId))
        {
            return new(GsxServiceCallStatus.AlreadySatisfied, $"{display} has already completed this turnaround.");
        }

        if (service.State != GsxServiceState.Callable)
        {
            return new(
                GsxServiceCallStatus.NotCallable,
                $"{display} is not callable right now (GSX reports '{service.SemanticState ?? "unknown"}').");
        }

        if (!service.CanTrigger)
        {
            return new(
                GsxServiceCallStatus.NotCallable,
                $"GSX reports {display} cannot be triggered right now.");
        }

        var outcome = await DispatchAsync(serviceId, display, cancellationToken).ConfigureAwait(false);
        if (action == GsxServiceAction.RequestRefuel && outcome.Status == GsxServiceCallStatus.Called)
        {
            // A direct refuel request IS the crew's confirmation of the current figure — it
            // releases the onFuelConfirmed hold so the sequencer never re-offers the truck.
            ConfirmCurrentFigure("request");
        }
        return outcome;
    }

    /// <summary>Second Refueling call in one turnaround: only when the fuel on board is short
    /// of the effective figure (25 kg tolerance, the tankering rule); otherwise the completed
    /// refuel stands.</summary>
    private async Task<GsxServiceCallOutcome> RequestRefuelTopUpAsync(
        GsxServiceInfo service,
        string display,
        CancellationToken cancellationToken)
    {
        var figure = EffectiveFigure();
        var fob = _fuelTotal.Value;
        if (!figure.HasValue)
        {
            return new(GsxServiceCallStatus.AlreadySatisfied, $"{display} has already completed this turnaround.");
        }

        if (fob >= figure.Kg - RefuelCore.TankeringToleranceKg)
        {
            return new(
                GsxServiceCallStatus.AlreadySatisfied,
                $"{display} has already completed — {fob:F0} kg on board meets the {figure.Kg:F0} kg figure; no top-up needed.");
        }

        if (service.State != GsxServiceState.Callable || !service.CanTrigger)
        {
            return new(
                GsxServiceCallStatus.NotCallable,
                $"GSX still reports the previous refuel finishing ('{service.SemanticState ?? service.State.ToString()}') — request the top-up again in a moment.");
        }

        _lifecycle.RearmCycle(GsxServiceIds.Refueling);
        RecordDecision(
            "refuel top-up",
            $"FOB {fob:F0} kg is below the {figure.Kg:F0} kg figure ({SourceLabel(figure.Source)}) after a completed refuel — Refueling re-called");

        var outcome = await DispatchAsync(GsxServiceIds.Refueling, display, cancellationToken).ConfigureAwait(false);
        if (outcome.Status == GsxServiceCallStatus.Called)
        {
            ConfirmCurrentFigure("top-up");
            return new(GsxServiceCallStatus.Called, $"Top-up requested — {fob:F0} kg on board, {figure.Kg:F0} kg ordered.");
        }
        return outcome;
    }

    private void ConfirmCurrentFigure(string source)
    {
        var figure = EffectiveFigure();
        if (figure.HasValue && !_fuelConfirmation.Confirmed)
        {
            _fuelConfirmation.Confirm(figure.Kg, source);
            RecordDecision("fuel confirmed", $"{figure.Kg:F0} kg ({SourceLabel(figure.Source)}) implied by the refuel {source}");
        }
    }

    /// <summary>Retract path for the operate toggles: only a service currently reading
    /// Active/Completed (connected) may be triggered — anything else means the requested state
    /// (retracted) already holds.</summary>
    private async Task<GsxServiceCallOutcome> RetractAsync(
        string serviceId,
        string display,
        CancellationToken cancellationToken)
    {
        var service = _api.Mirror.Services.GetValueOrDefault(serviceId);
        if (service is null || service.State is not (GsxServiceState.Active or GsxServiceState.Completed))
        {
            return new(GsxServiceCallStatus.AlreadySatisfied, $"The {display} is not connected — nothing to retract.");
        }

        if (!service.CanTrigger)
        {
            // Spec §3: retract via trigger only when canTrigger; the gate-menu operate intent
            // is the manual fallback.
            return new(
                GsxServiceCallStatus.NotCallable,
                $"GSX cannot retract the {display} right now — use the GSX gate menu.");
        }

        return await DispatchAsync(serviceId, display, cancellationToken).ConfigureAwait(false);
    }

    private async Task<GsxServiceCallOutcome> DispatchAsync(
        string serviceId,
        string display,
        CancellationToken cancellationToken)
    {
        // Retry-once (#76): an on-demand call has no sequencer re-offering it, so a silent
        // drop gets one automatic re-send before the user is told to try again.
        var dispatch = await _slot
            .TryDispatchAsync(
                new GsxTriggerRequest(serviceId, "command") { RetryOnce = true },
                cancellationToken)
            .ConfigureAwait(false);
        return dispatch.Status switch
        {
            GsxTriggerDispatchStatus.Dispatched => new(
                GsxServiceCallStatus.Called,
                $"{Capitalize(display)} requested — awaiting GSX confirmation."),
            GsxTriggerDispatchStatus.Busy when string.Equals(
                    dispatch.BusyServiceId, serviceId, StringComparison.OrdinalIgnoreCase)
                => new(GsxServiceCallStatus.AlreadySatisfied, $"{Capitalize(display)} has already been called — awaiting GSX."),
            GsxTriggerDispatchStatus.Busy => new(
                GsxServiceCallStatus.NotCallable,
                $"Waiting for GSX to confirm {dispatch.BusyServiceId ?? "another service"} — "
                + "one call goes out at a time; try again in a few seconds."),
            _ => new(GsxServiceCallStatus.Rejected, $"GSX rejected the call ({dispatch.RejectCode ?? "unknown"})."),
        };
    }

    /// <summary>Connect requests yield to the ground-prep coordinator only while it is actually
    /// in its window (Preflight/ColdAndDark with the auto-connect feature on) — PrepComplete is
    /// also false in unrelated phases (it resets after flight), which must not block an arrival
    /// jetway call.</summary>
    private bool GroundPrepOwnsJetwayStairs()
    {
        var options = _options.CurrentValue;
        return options.AutomationEnabled
            && options.AutoConnectJetwayOrStairs
            && !_groundPrep.PrepComplete
            && _flightPhase.CurrentPhase.IsAtGate();
    }

    private static bool IsToggle(GsxServiceAction action)
        => action is GsxServiceAction.RequestJetway or GsxServiceAction.RequestStairs;

    private static string ServiceIdFor(GsxServiceAction action) => action switch
    {
        GsxServiceAction.RequestRefuel => GsxServiceIds.Refueling,
        GsxServiceAction.RequestCatering => GsxServiceIds.Catering,
        GsxServiceAction.RequestBoarding => GsxServiceIds.Boarding,
        GsxServiceAction.RequestDeboarding => GsxServiceIds.Deboarding,
        GsxServiceAction.RequestJetway => JetwayServiceId,
        GsxServiceAction.RequestStairs => StairsServiceId,
        GsxServiceAction.RequestGpu => GsxServiceIds.Gpu,
        GsxServiceAction.RequestDeice => GsxServiceIds.DeIce,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "No direct service id."),
    };

    private static string DisplayNameFor(GsxServiceAction action) => action switch
    {
        GsxServiceAction.RequestRefuel => "refueling",
        GsxServiceAction.RequestCatering => "catering",
        GsxServiceAction.RequestBoarding => "boarding",
        GsxServiceAction.RequestDeboarding => "deboarding",
        GsxServiceAction.RequestJetway => "jetway",
        GsxServiceAction.RequestStairs => "stairs",
        GsxServiceAction.RequestGpu => "the GPU",
        GsxServiceAction.RequestDeice => "de-icing",
        _ => action.ToString(),
    };

    private static string Capitalize(string text)
        => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static string Decapitalize(string text)
        => text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text[1..];
}
