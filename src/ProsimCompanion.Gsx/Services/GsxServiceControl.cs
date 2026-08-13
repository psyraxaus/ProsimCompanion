using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Gsx.Automation;
using ProsimCompanion.Gsx.Protocol;
using ProsimCompanion.Gsx.Sync;

namespace ProsimCompanion.Gsx.Services;

/// <summary>
/// Implements the Core <see cref="IGsxServiceControl"/> seam: pre-flight checks against the
/// state mirror + lifecycle cycles (never raw ordinals, never the menu), then dispatch through
/// <see cref="IGsxTriggerDispatcher"/> — the SAME serialized single-slot service.trigger path
/// the departure automation uses, so a user command can never rapid-fire GSX alongside it.
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
    private readonly IGsxTriggerDispatcher _dispatcher;
    private readonly IGsxGroundPrepStatus _groundPrep;
    private readonly IFlightPhaseSource _flightPhase;
    private readonly IGsxFlightPlanStatus _flightPlan;
    private readonly SimSessionStore _simSession;
    private readonly IOptionsMonitor<GsxOptions> _options;
    private readonly ILogger<GsxServiceControl> _logger;
    private readonly IDataRefSubscription _jetwayLvar;

    public GsxServiceControl(
        IGsxRemoteApi api,
        GsxServiceLifecycleTracker lifecycle,
        IGsxTriggerDispatcher dispatcher,
        IGsxGroundPrepStatus groundPrep,
        IFlightPhaseSource flightPhase,
        IGsxFlightPlanStatus flightPlan,
        SimSessionStore simSession,
        ISimVars simVars,
        IOptionsMonitor<GsxOptions> options,
        ILogger<GsxServiceControl> logger)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(groundPrep);
        ArgumentNullException.ThrowIfNull(flightPhase);
        ArgumentNullException.ThrowIfNull(flightPlan);
        ArgumentNullException.ThrowIfNull(simSession);
        ArgumentNullException.ThrowIfNull(simVars);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _api = api;
        _lifecycle = lifecycle;
        _dispatcher = dispatcher;
        _groundPrep = groundPrep;
        _flightPhase = flightPhase;
        _flightPlan = flightPlan;
        _simSession = simSession;
        _options = options;
        _logger = logger;

        _jetwayLvar = simVars.Subscribe(GsxLvarNames.Jetway, "number", DataRefTier.Normal);
    }

    public void Dispose() => _jetwayLvar.Dispose();

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
            _ => await RequestAsync(action, cancellationToken).ConfigureAwait(false),
        };
    }

    private async Task<GsxServiceCallOutcome> RequestAsync(
        GsxServiceAction action,
        CancellationToken cancellationToken)
    {
        var serviceId = ServiceIdFor(action);
        var display = DisplayNameFor(action);

        // Flight-plan gate (ADR-0006 / issue #50): the sequencer has always held these until a
        // plan exists; the on-demand path (voice, web, API) now applies the same rule instead
        // of going straight to GSX. Departure-prep phases only — an arrival deboarding or a
        // turnaround already in progress is never plan-gated here.
        if (action is GsxServiceAction.RequestRefuel or GsxServiceAction.RequestCatering or GsxServiceAction.RequestBoarding
            && _options.CurrentValue.RequireOfpBeforeDeparture
            && _flightPhase.CurrentPhase is FlightPhase.Preflight or FlightPhase.ColdAndDark
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
                && (int)_jetwayLvar.GetValue(0.0) == JetwayLvarNotPresent)
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

        return await DispatchAsync(serviceId, display, cancellationToken).ConfigureAwait(false);
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
        var dispatch = await _dispatcher
            .TryDispatchServiceTriggerAsync(serviceId, "command", cancellationToken)
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
            && _flightPhase.CurrentPhase is FlightPhase.Preflight or FlightPhase.ColdAndDark;
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
}
