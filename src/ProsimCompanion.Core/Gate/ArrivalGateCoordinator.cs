using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Airports.Parking;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Gate;

/// <summary>Point-in-time view of the arrival-gate workflow for the OFP page: the queued
/// gate, whether it has been dispatched, one status line per target (ATC via
/// SayIntentions, GSX via gate.select), and what the parking catalogue knows about the
/// stand ("GSX knows it as Gate 40 (Apron 1W …) · jetway · max span 65 m"; empty while
/// unknown). <paramref name="Restored"/> is true while the pending gate came back from the
/// previous run's persisted queue (issue #102) and nobody has confirmed or cancelled since —
/// the OFP page says so, because after an in-flight restart the page's own OFP is usually
/// not loaded and the gate would otherwise be invisible.</summary>
public sealed record ArrivalGateView(string? PendingGate, bool Sent, string AtcStatus, string GsxStatus, string StandInfo = "", bool Restored = false)
{
    public static ArrivalGateView Empty { get; } = new(null, false, "", "");
}

/// <summary>
/// Two-stage arrival-gate coordinator (Prosim2GSX semantics): Confirm queues the gate and
/// reaching cruise — or, since issue #102, any later phase of the leg when the cruise edge
/// is already history — auto-fires it once to BOTH targets: GSX (arm-then-dispatch via
/// <see cref="IGsxGateControl"/>) and SayIntentions ATC (assignGate, airport = OFP
/// destination); Send Now fires both immediately. Either target may be absent from the
/// composition or inactive at send time — the other still fires and its status line
/// explains (degrade, not fail). Queue/trigger rules live in the pure
/// <see cref="ArrivalGatePlan"/>; this class adds locking, dispatch, persistence across an
/// app restart and status text. The session events <c>arrival-gate-confirmed</c> and
/// <c>arrival-gate-dispatched</c> are the probe evidence
/// (<c>arrival-gate-dispatched-before-touchdown</c> in docs/agents/verification-probes.json).
/// </summary>
public sealed class ArrivalGateCoordinator : IDisposable
{
    private readonly IFlightPhaseSource _phase;
    private readonly OfpStore _ofp;
    private readonly IGsxGateControl? _gsx;
    private readonly ISayIntentionsGateAssign? _atc;
    private readonly ILogger<ArrivalGateCoordinator> _logger;
    private readonly ArrivalGateStateFile? _store;
    private readonly IAirportParkingCatalog? _parkings;
    private readonly JsonlEventLog? _eventLog;
    private readonly object _lock = new();
    private readonly ArrivalGatePlan _queue = new();
    private string _atcStatus = "";
    private string _gsxStatus = "";
    private string _standInfo = "";
    private string? _standInfoGate;
    private bool _restored;

    /// <param name="store">Restart persistence for the queue (2026-09-20: two in-flight app
    /// restarts lost the confirmed gate). Null (tests, or a composition without it) keeps the
    /// queue in memory only.</param>
    /// <param name="parkings">The airport parking catalogue (2026-10-04) — describes the
    /// confirmed stand on the OFP page so a template-renamed gate ("W40" → "Gate 40") is
    /// visible before the send. Null keeps the view without the line.</param>
    /// <param name="eventLog">The session event log for the probe evidence (issue #102).
    /// Null (tests) records nothing.</param>
    public ArrivalGateCoordinator(
        IFlightPhaseSource phase,
        OfpStore ofp,
        IGsxGateControl? gsxGate,
        ISayIntentionsGateAssign? sayIntentions,
        ILogger<ArrivalGateCoordinator> logger,
        ArrivalGateStateFile? store = null,
        IAirportParkingCatalog? parkings = null,
        JsonlEventLog? eventLog = null)
    {
        ArgumentNullException.ThrowIfNull(phase);
        ArgumentNullException.ThrowIfNull(ofp);
        ArgumentNullException.ThrowIfNull(logger);

        _phase = phase;
        _ofp = ofp;
        _gsx = gsxGate;
        _atc = sayIntentions;
        _logger = logger;
        _store = store;
        _parkings = parkings;
        _eventLog = eventLog;

        _phase.PhaseChanged += OnPhaseChanged;
    }

    /// <summary>The most recent stand lookup, awaitable by tests; UI callers never block on it.</summary>
    public Task LastStandLookup { get; private set; } = Task.CompletedTask;

    /// <summary>What the catalogue knows about a gate token at the OFP destination, as one
    /// line; empty when no source knows the airport or the token matches no stand. Used by
    /// the OFP page for the live hint under the input and by the FO's answer.</summary>
    public async Task<string> DescribeAsync(string? gate, CancellationToken cancellationToken = default)
    {
        var token = ArrivalGatePlan.Normalize(gate);
        var icao = ArrivalGatePlan.Normalize(_ofp.Current?.DestinationIcao);
        if (_parkings is null || token is null || icao is null)
        {
            return "";
        }

        try
        {
            var catalogue = await _parkings.GetAsync(icao, cancellationToken: cancellationToken).ConfigureAwait(false);
            var match = ParkingTokenResolver.Resolve(catalogue, token);
            if (match is not null)
            {
                return ParkingDescription.Describe(match);
            }

            return catalogue is null
                ? ""
                : $"no stand '{token}' among the {catalogue.Parkings.Count} the profile/scenery list for {icao}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stand lookup for {Gate} at {Icao} failed", token, icao);
            return "";
        }
    }

    private void RefreshStandInfo(string? gate)
    {
        lock (_lock)
        {
            if (string.Equals(_standInfoGate, gate, StringComparison.Ordinal) && !string.IsNullOrEmpty(_standInfo))
            {
                return;
            }

            _standInfoGate = gate;
            _standInfo = "";
        }

        if (gate is null)
        {
            return;
        }

        LastStandLookup = Task.Run(async () =>
        {
            var info = await DescribeAsync(gate).ConfigureAwait(false);
            lock (_lock)
            {
                if (!string.Equals(_standInfoGate, gate, StringComparison.Ordinal))
                {
                    return;
                }

                _standInfo = info;
            }

            if (info.Length > 0)
            {
                _logger.LogInformation("Arrival gate {Gate}: {StandInfo}", gate, info);
            }

            RaiseChanged();
        });
    }

    /// <summary>
    /// Re-queues the gate persisted by a previous run of this flight, and re-fires it when
    /// the flight is already past the cruise transition (or the previous run had fired it —
    /// GSX's armed request lives in that process's memory and is gone). Called once at
    /// startup by the core bootstrap. Returns what was done so the caller can record it.
    /// <para>Issue #102 (2026-08-22 EGLL→LGAV): the pilot sent "A05" in the descent, GSX held
    /// it armed (destination not loaded yet), the app restarted for an update and the request
    /// died with the process. The restore re-arms GSX; a queue restored before the cruise entry
    /// (phase Unknown at startup is the normal case) fires on the engine's first commit into any
    /// auto-fire phase, because <see cref="ArrivalGatePlan.TakeAuto"/> no longer keys on the
    /// Cruise edge alone.</para>
    /// </summary>
    public (ArrivalGateRestoreAction Action, string? Gate) Restore()
    {
        if (_store is null)
        {
            return (ArrivalGateRestoreAction.Ignore, null);
        }

        var state = _store.Load();
        var ofp = _ofp.Current;
        var action = ArrivalGateRestorePlan.Decide(
            state, DateTimeOffset.UtcNow, _phase.CurrentPhase, ofp?.DestinationIcao, ofp?.FlightNumber);
        if (action == ArrivalGateRestoreAction.Ignore)
        {
            if (state is not null)
            {
                _logger.LogInformation(
                    "Persisted arrival gate {Gate} for {Destination} (flight {Flight}, saved {SavedAt:u}) ignored — stale or another flight",
                    state.Gate, state.DestinationIcao ?? "?", state.FlightNumber ?? "?", state.SavedAtUtc);
                _store.Clear();
            }
            return (ArrivalGateRestoreAction.Ignore, null);
        }

        string? gate;
        bool atcOrigin;
        lock (_lock)
        {
            gate = _queue.Queue(state!.Gate);
            if (gate is null)
            {
                return (ArrivalGateRestoreAction.Ignore, null);
            }

            _restored = true;
            _atcOrigin = atcOrigin = state.AtcOrigin;
            _gsxStatus = _gsx is null
                ? "GSX gate control unavailable — GSX push will be skipped."
                : "Restored after restart — queued for GSX.";
            _atcStatus = atcOrigin
                ? "Assigned by SayIntentions ATC before the restart — nothing to send back."
                : _atc is null
                    ? "SayIntentions integration not available — ATC push will be skipped."
                    : "Restored after restart.";
        }

        _logger.LogInformation(
            "Arrival gate {Gate} restored after restart ({Action}, phase {Phase}, origin {Origin})",
            gate, action, _phase.CurrentPhase, atcOrigin ? "ATC" : "pilot");
        RaiseChanged();
        RefreshStandInfo(gate);

        switch (action)
        {
            case ArrivalGateRestoreAction.DispatchBoth:
                lock (_lock)
                {
                    _queue.TakeManual();
                }
                Persist(fired: true);
                LastDispatch = DispatchAsync(gate, includeAtc: !atcOrigin, trigger: "restore");
                break;

            case ArrivalGateRestoreAction.DispatchGsxOnly:
                lock (_lock)
                {
                    _queue.TakeManual();
                }
                Persist(fired: true);
                LastDispatch = DispatchAsync(gate, includeAtc: false, trigger: "restore");
                if (!atcOrigin)
                {
                    SetAtcStatus("ATC assignment already sent before the restart.");
                }
                break;

            default:
                Persist(fired: false);
                TryAutoFire(_phase.CurrentPhase);
                break;
        }

        return (action, gate);
    }

    /// <summary>Raised after any state change, on the writer's thread — consumers marshal to
    /// their own context (<c>InvokeAsync</c> in Blazor components).</summary>
    public event EventHandler? Changed;

    /// <summary>The most recent dispatch operation. Exposed so tests (and diagnostics) can
    /// await completion; UI callers never block on it.</summary>
    public Task LastDispatch { get; private set; } = Task.CompletedTask;

    public ArrivalGateView Snapshot()
    {
        lock (_lock)
        {
            return new(_queue.PendingGate, _queue.Fired, _atcStatus, _gsxStatus, _standInfo, _restored);
        }
    }

    /// <summary>Queues the gate for auto-send at cruise. When already at or past the cruise
    /// entry the transition we would fire on has passed, so the queue fires immediately
    /// (issue #102: "A05" confirmed in the descent used to sit queued until Send Now).</summary>
    public void Confirm(string gate)
    {
        string? queued;
        var phase = _phase.CurrentPhase;
        lock (_lock)
        {
            queued = _queue.Queue(gate);
            if (queued is null)
            {
                return;
            }

            _atcOrigin = false;
            _restored = false;
            _atcStatus = _atc is null
                ? "SayIntentions integration not available — ATC push will be skipped."
                : _atc.IsActive
                    ? "Queued — sends to ATC at cruise (or Send Now)."
                    : "SayIntentions not active — ATC assignment will be skipped.";
            _gsxStatus = _gsx is null
                ? "GSX gate control unavailable — GSX push will be skipped."
                : "Queued — sends to GSX at cruise (or Send Now).";
        }

        _logger.LogInformation("Arrival gate {Gate} confirmed ({Phase}) — auto-sends at cruise or later", queued, phase);
        _eventLog?.Record("arrival-gate-confirmed", new { gate = queued, phase = phase.ToString(), origin = "pilot" });
        Persist(fired: false);
        RaiseChanged();
        RefreshStandInfo(queued);
        TryAutoFire(phase);
    }

    /// <summary>
    /// Queues a gate that ATC (SayIntentions) assigned — 2026-10-08, the reverse of the
    /// assignGate push. Only GSX is a target: SayIntentions already holds the gate, so the
    /// ATC half is marked "from ATC" instead of being sent back. Past the cruise entry
    /// (reassigned on approach, "taxi to gate …" after landing) the gate goes to GSX at once,
    /// because the cruise edge the normal queue fires on has passed. The origin IS persisted
    /// (issue #102): a restored gate of ATC origin goes to GSX only, so SayIntentions is never
    /// sent its own assignment back after a restart.
    /// </summary>
    public void ConfirmFromAtc(string gate)
    {
        string? queued;
        var phase = _phase.CurrentPhase;
        var fireNow = ArrivalGatePlan.IsAutoFirePhase(phase);
        lock (_lock)
        {
            queued = _queue.Queue(gate);
            if (queued is null)
            {
                return;
            }

            _atcOrigin = true;
            _restored = false;
            _atcStatus = "Assigned by SayIntentions ATC — nothing to send back.";
            _gsxStatus = _gsx is null
                ? "GSX gate control unavailable — GSX push will be skipped."
                : fireNow
                    ? "From ATC — sending to GSX now."
                    : "From ATC — sends to GSX at cruise (or Send Now).";
            if (fireNow)
            {
                _queue.TakeManual();
            }
        }

        _logger.LogInformation(
            "Arrival gate {Gate} assigned by SayIntentions ATC ({Phase}) — {Action}",
            queued, phase, fireNow ? "sending to GSX now" : "queued for cruise");
        _eventLog?.Record("arrival-gate-confirmed", new { gate = queued, phase = phase.ToString(), origin = "atc" });
        Persist(fired: fireNow);
        RaiseChanged();
        RefreshStandInfo(queued);
        if (fireNow)
        {
            LastDispatch = DispatchAsync(queued, includeAtc: false, trigger: "atc");
        }
        else
        {
            TryAutoFire(phase);
        }
    }

    /// <summary>True while the queued gate came from ATC: dispatch skips the SayIntentions
    /// push. Cleared by a pilot Confirm / Send Now with a gate / Cancel.</summary>
    private bool _atcOrigin;

    /// <summary>Fires both targets now: an explicit gate wins over the queued one; with
    /// neither, nothing happens. Returns the dispatch task (UI callers may discard it).</summary>
    public Task SendNow(string? gate = null)
    {
        string? send;
        bool includeAtc;
        lock (_lock)
        {
            if (ArrivalGatePlan.Normalize(gate).Length > 0)
            {
                _atcOrigin = false; // the pilot typed a gate — theirs, send it everywhere
                _restored = false;
            }

            send = _queue.TakeManual(gate);
            includeAtc = !_atcOrigin;
        }

        if (send is null)
        {
            return Task.CompletedTask;
        }

        _logger.LogInformation("Arrival gate {Gate} — Send Now", send);
        Persist(fired: true);
        RefreshStandInfo(send);
        LastDispatch = DispatchAsync(send, includeAtc, trigger: "sendNow");
        return LastDispatch;
    }

    /// <summary>Clears the queue (re-queue allowed after) and cancels any armed GSX request.</summary>
    public void Cancel()
    {
        lock (_lock)
        {
            _queue.Clear();
            _atcOrigin = false;
            _restored = false;
            _atcStatus = "";
            _gsxStatus = "";
            _standInfo = "";
            _standInfoGate = null;
        }

        try
        {
            _gsx?.Cancel();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GSX gate cancel failed");
        }

        _store?.Clear();
        RaiseChanged();
    }

    public void Dispose() => _phase.PhaseChanged -= OnPhaseChanged;

    private void OnPhaseChanged(object? sender, FlightPhaseChangedEventArgs e)
    {
        if (e.Current == FlightPhase.Shutdown)
        {
            // The leg is over: the persisted queue must not resurface on the next flight's
            // startup as this flight's gate (the in-memory queue stays for the OFP page).
            _store?.Clear();
            return;
        }

        TryAutoFire(e.Current);
    }

    /// <summary>The auto-fire on a committed phase: the pure plan decides (cruise entry or any
    /// later phase, once per queued gate). Covers the normal Climb → Cruise edge, a short leg
    /// that commits Descent without ever committing Cruise, a Confirm made past the cruise
    /// entry, and the startup case after an in-flight restart where the engine's first commit
    /// goes Unknown → Descent/Approach (issue #102) — one rule instead of special cases.</summary>
    private void TryAutoFire(FlightPhase phase)
    {
        string? gate;
        bool includeAtc;
        lock (_lock)
        {
            includeAtc = !_atcOrigin;
            gate = _queue.TakeAuto(phase);
        }

        if (gate is null)
        {
            return;
        }

        _logger.LogInformation("{Phase} — auto-sending arrival gate {Gate}", phase, gate);
        Persist(fired: true);
        LastDispatch = DispatchAsync(gate, includeAtc, trigger: "auto");
    }

    /// <summary>Mirrors the queue to disk (no-op without a store). Never throws.</summary>
    private void Persist(bool fired)
    {
        if (_store is null)
        {
            return;
        }

        string? gate;
        bool atcOrigin;
        lock (_lock)
        {
            gate = _queue.PendingGate;
            atcOrigin = _atcOrigin;
        }

        if (gate is null)
        {
            _store.Clear();
            return;
        }

        var ofp = _ofp.Current;
        var destination = ArrivalGatePlan.Normalize(ofp?.DestinationIcao);
        var flightNumber = ArrivalGatePlan.Normalize(ofp?.FlightNumber);
        _store.Save(new ArrivalGateState(
            gate,
            destination.Length > 0 ? destination : null,
            fired,
            DateTimeOffset.UtcNow,
            atcOrigin,
            flightNumber.Length > 0 ? flightNumber : null));
    }

    /// <param name="trigger">What fired the dispatch — auto (phase), sendNow, restore, atc —
    /// recorded on the <c>arrival-gate-dispatched</c> session event for the probe.</param>
    private async Task DispatchAsync(string gate, bool includeAtc, string trigger)
    {
        _eventLog?.Record("arrival-gate-dispatched", new
        {
            gate,
            phase = _phase.CurrentPhase.ToString(),
            trigger,
            includeAtc,
            gsx = _gsx is not null,
        });

        // GSX first — RequestGate arms and only dispatches once GSX has the destination
        // airport loaded, so firing from cruise is safe (predecessor behaviour).
        if (_gsx is null)
        {
            SetGsxStatus("GSX gate control unavailable — skipped.");
        }
        else
        {
            try
            {
                _gsx.RequestGate(gate);
                SetGsxStatus($"Sent to GSX {DateTimeOffset.UtcNow:HH:mm:ss}Z — armed (progress on the GSX line).");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GSX gate request failed");
                SetGsxStatus($"GSX request failed: {ex.Message}");
            }
        }

        if (!includeAtc)
        {
            return;
        }

        if (_atc is null)
        {
            SetAtcStatus("SayIntentions integration not available — ATC push skipped.");
            return;
        }

        var airport = ArrivalGatePlan.Normalize(_ofp.Current?.DestinationIcao);
        if (airport.Length == 0)
        {
            SetAtcStatus("No destination ICAO (load an OFP) — ATC assignment skipped.");
            return;
        }

        SetAtcStatus("Sending to ATC…");
        try
        {
            var result = await _atc.AssignGateAsync(airport, gate).ConfigureAwait(false);
            SetAtcStatus(result.Ok
                ? $"ATC assignment confirmed: {result.Detail} at {DateTimeOffset.UtcNow:HH:mm:ss}Z"
                : result.Skipped
                    ? result.Detail
                    : $"ATC assignment failed: {result.Detail}");
        }
        catch (Exception ex)
        {
            // Degrade, not fail: the GSX half already fired; the ATC line carries the reason.
            _logger.LogWarning(ex, "SayIntentions assignGate dispatch failed");
            SetAtcStatus($"ATC assignment failed: {ex.Message}");
        }
    }

    private void SetAtcStatus(string status)
    {
        lock (_lock)
        {
            _atcStatus = status;
        }

        RaiseChanged();
    }

    private void SetGsxStatus(string status)
    {
        lock (_lock)
        {
            _gsxStatus = status;
        }

        RaiseChanged();
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
