using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Airports.Parking;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Gsx.Menu;
using ProsimCompanion.Gsx.Mirror;

namespace ProsimCompanion.Gsx.Gate;

public enum GsxGateRequestStatus
{
    Idle,
    Armed,
    Dispatching,

    /// <summary>gate.select succeeded; awaiting SetGate_* readback confirmation.</summary>
    Assigned,

    /// <summary>Readback matched the requested gate.</summary>
    Confirmed,

    /// <summary>Success reported but the 60 s readback window expired — never a failure.</summary>
    AssignedUnconfirmed,

    Failed,
}

/// <summary>
/// Arrival-gate assignment with the arm-then-dispatch model and single-auto-retry ladder
/// (docs/integrations/gsx-remote-api.md §6). The dispatch decision is the pure
/// <see cref="GateDispatchPlanner"/>: in the air (cruise → approach) GSX is made to load the
/// destination through its own "Select airport" menu, then <c>gate.select</c> goes out with the
/// token as typed — GSX's search resolves "545R" against "Stand 545R" by suffix (Handler
/// Scripts Developer Guide, selectGate). On the ground the send happens the moment GSX loads
/// the airport itself, while still rolling; once parked it is too late by GSX's own rule.
/// Success is provisional until the SetGate_* LVAR readback matches (60 s window, checked
/// immediately too). A Couatl restart re-arms the last request.
/// </summary>
public sealed class GsxGateSelectionService : Core.State.IGsxGateControl, IGsxAssignedGateSource, IDisposable
{
    private static readonly TimeSpan ConfirmationWindow = TimeSpan.FromSeconds(60);

    /// <summary>Minimum quiet time before an identical failed gate.select may be re-dispatched
    /// (issue #75: mirror updates arrive about once a second and re-invoke the dispatcher — a
    /// failed request re-fired 947 ms after its own failure, twice within the same second).</summary>
    private static readonly TimeSpan FailedRetryBackoff = TimeSpan.FromSeconds(10);

    /// <summary>GSX's in-flight airport list page (Prosim2GSX GsxParkingSelector archaeology:
    /// the title is exactly this; rows carry the ICAO).</summary>
    internal const string AirportSelectTitle = "Select airport";

    /// <summary>The destination context arrives as a handlerData/airport patch after the pick;
    /// GSX loads the airport in the background, so allow well beyond the 5 s default.</summary>
    private static readonly TimeSpan AirportLoadTimeout = TimeSpan.FromSeconds(20);

    /// <summary>GSX's own parking question — "Select Position at EFHK/Vantaa" (issue #156).</summary>
    internal const string PositionSelectTitle = "Select Position at";

    /// <summary>GSX's positions page under a facility group — "All Apron 1W (Gates W34-W48)  positions".</summary>
    internal const string PositionsPageTitle = "All ";

    private const int PositionMenuMaxAttempts = 2;

    private readonly IGsxRemoteApi _api;
    private readonly GsxMenuIntentExecutor _executor;
    private readonly IFlightPhaseSource _flightState;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<GsxGateSelectionService> _logger;
    private readonly IDataRefSubscription<string?> _destination;
    private readonly IDataRefSubscription<int> _gateName;
    private readonly IDataRefSubscription<int> _gateNumber;
    private readonly IDataRefSubscription<int> _gateSuffix;
    private readonly object _gate = new();
    private string? _requestedGate;
    private bool _retriedOnce;
    private bool _dispatching;
    private bool _tooLateReported;
    private string? _lastFailedGate;
    private long _lastFailedAtTicks;
    private DateTimeOffset? _lastAirportPickAtUtc;
    private int _airportPickAttempts;
    private int _positionMenuAttempts;
    private bool _answeringPositionMenu;
    private GsxAssignedGate? _assigned;
    private readonly IOptionsMonitor<GsxOptions>? _options;
    private readonly IAirportParkingCatalog? _parkings;
    private Timer? _confirmationTimer;

    public GsxGateSelectionService(
        IGsxRemoteApi api,
        GsxMenuIntentExecutor executor,
        IFlightPhaseSource flightState,
        IProsimDataRefs prosim,
        ISimVars simVars,
        JsonlEventLog eventLog,
        ILogger<GsxGateSelectionService> logger,
        IOptionsMonitor<GsxOptions>? options = null,
        IAirportParkingCatalog? parkings = null)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(flightState);
        ArgumentNullException.ThrowIfNull(prosim);
        ArgumentNullException.ThrowIfNull(simVars);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _api = api;
        _executor = executor;
        _flightState = flightState;
        _eventLog = eventLog;
        _logger = logger;
        _options = options;
        _parkings = parkings;

        _destination = prosim.Subscribe(ProsimDataRefNames.FmsDestination);
        _gateName = simVars.Subscribe(GsxLvarNames.SetGateName);
        _gateNumber = simVars.Subscribe(GsxLvarNames.SetGateNumber);
        _gateSuffix = simVars.Subscribe(GsxLvarNames.SetGateSuffix);

        _api.ReadinessChanged += OnStateChanged;
        _api.Mirror.Updated += OnMirrorUpdated;
        _api.Mirror.SidChanged += OnSidChanged;
        _flightState.PhaseChanged += OnPhaseChanged;
        _destination.ValueChanged += OnDataChanged;
        _gateName.ValueChanged += OnReadbackChanged;
        _gateNumber.ValueChanged += OnReadbackChanged;
        _gateSuffix.ValueChanged += OnReadbackChanged;
    }

    public GsxGateRequestStatus Status { get; private set; } = GsxGateRequestStatus.Idle;

    public string? RequestedGate => _requestedGate;

    public string StatusDetail { get; private set; } = "";

    /// <inheritdoc />
    public GsxAssignedGate? AssignedGate
        => Status is GsxGateRequestStatus.Assigned or GsxGateRequestStatus.Confirmed or GsxGateRequestStatus.AssignedUnconfirmed
            ? _assigned
            : null;

    /// <summary>Raised on any status change, on arbitrary threads.</summary>
    public event Action? Changed;

    /// <summary>Arms a gate request (a display token as typed in GSX's gate search, e.g. "B12")
    /// and dispatches as soon as the preconditions hold.</summary>
    public void RequestGate(string gate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gate);

        lock (_gate)
        {
            _requestedGate = gate.Trim().ToUpperInvariant();
            _assigned = null;
            _retriedOnce = false;
            _tooLateReported = false;
            _airportPickAttempts = 0;
            _lastAirportPickAtUtc = null;
            _positionMenuAttempts = 0;
        }
        SetStatus(GsxGateRequestStatus.Armed, $"armed for {gate}");
        _ = TryDispatchAsync();
    }

    public void Cancel()
    {
        lock (_gate)
        {
            _requestedGate = null;
            _assigned = null;
        }
        _confirmationTimer?.Dispose();
        _confirmationTimer = null;
        SetStatus(GsxGateRequestStatus.Idle, "cancelled");
    }

    public void Dispose()
    {
        _api.ReadinessChanged -= OnStateChanged;
        _api.Mirror.Updated -= OnMirrorUpdated;
        _api.Mirror.SidChanged -= OnSidChanged;
        _flightState.PhaseChanged -= OnPhaseChanged;
        _destination.ValueChanged -= OnDataChanged;
        _gateName.ValueChanged -= OnReadbackChanged;
        _gateNumber.ValueChanged -= OnReadbackChanged;
        _gateSuffix.ValueChanged -= OnReadbackChanged;
        _confirmationTimer?.Dispose();
        _destination.Dispose();
        _gateName.Dispose();
        _gateNumber.Dispose();
        _gateSuffix.Dispose();
    }

    internal async Task TryDispatchAsync()
    {
        string requested;
        GateDispatchStep step;
        string destination;
        lock (_gate)
        {
            if (_requestedGate is null || _dispatching
                || Status is GsxGateRequestStatus.Assigned or GsxGateRequestStatus.Confirmed or GsxGateRequestStatus.AssignedUnconfirmed)
            {
                return;
            }

            // Failed-request backoff (issue #75): don't re-fire the identical request on every
            // state-change event. An explicit RequestGate re-arms (Status leaves Failed) and
            // bypasses this; after the quiet period the automatic retry path resumes.
            if (Status == GsxGateRequestStatus.Failed
                && string.Equals(_lastFailedGate, _requestedGate, StringComparison.Ordinal)
                && TimeSpan.FromMilliseconds(Environment.TickCount64 - _lastFailedAtTicks) < FailedRetryBackoff)
            {
                return;
            }

            var view = _flightState.Snapshot();
            destination = _destination.Value?.Trim() ?? "";
            step = GateDispatchPlanner.Decide(new GateDispatchInputs(
                _api.Readiness == GsxReadiness.Ready,
                _api.Mirror.AirportIcao,
                destination,
                view.Phase,
                view.Data,
                DateTimeOffset.UtcNow,
                _lastAirportPickAtUtc,
                _airportPickAttempts));

            switch (step)
            {
                case GateDispatchStep.Wait:
                    return; // stay armed; the state-change wiring re-invokes us

                case GateDispatchStep.TooLate:
                    if (_tooLateReported)
                    {
                        return;
                    }
                    _tooLateReported = true;
                    break;

                case GateDispatchStep.PickAirport:
                    _airportPickAttempts++;
                    _lastAirportPickAtUtc = DateTimeOffset.UtcNow;
                    break;
            }

            _dispatching = true;
            requested = _requestedGate;
        }

        try
        {
            switch (step)
            {
                case GateDispatchStep.TooLate:
                    // selectGate refuses while parked with services engaged (Handler Scripts
                    // Developer Guide) and the refusal surfaces as not_found — say so instead
                    // of sending, so nobody chases stand names again (2026-08-22 EGLL 313,
                    // 2026-09-13 LIRF 829).
                    _eventLog.Record("gsx-gate-too-late", new { gate = requested, airport = _api.Mirror.AirportIcao });
                    Fail("too late — GSX already has the aircraft parked at a stand; pick the gate in the GSX menu");
                    return;

                case GateDispatchStep.PickAirport:
                    SetStatus(GsxGateRequestStatus.Armed, $"loading {destination} in GSX (in-flight airport pick, attempt {_airportPickAttempts})");
                    if (!await PickAirportInFlightAsync(destination).ConfigureAwait(false))
                    {
                        return; // stay armed; the backoff paces the next attempt
                    }
                    break;
            }

            SetStatus(GsxGateRequestStatus.Dispatching, $"selecting {requested}");
            await DispatchLadderAsync(requested).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Gate selection dispatch failed unexpectedly");
            SetStatus(GsxGateRequestStatus.Failed, ex.Message);
        }
        finally
        {
            lock (_gate)
            {
                _dispatching = false;
            }
        }
    }

    /// <summary>
    /// The in-flight airport pick (the Handler Scripts guide's onSelectGateInFlight moment):
    /// GSX menu → "Select airport" page → the row carrying the destination ICAO. Success is
    /// GSX reporting the destination as its loaded airport (handlerData/airport patch). The
    /// row is matched by ICAO text, never by ordinal (§5 rule; the predecessor's row-2
    /// fallback is deliberately not carried). A menu we opened is closed again on failure;
    /// on success the gate.select that follows closes it.
    /// <para>2026-10-04 EFHK→LKPR (issue #157): in flight GSX's ROOT menu is itself titled
    /// "Select airport" and lists "LKPR Ruzyne at 592.70 nm [PLANNED]" — there is no root
    /// entry to pick first. The executor runs the row pick directly when the opened page is
    /// already this one; the root intent stays for a GSX that shows a root menu first.</para>
    /// </summary>
    /// <summary>
    /// The observable effect of the in-flight airport row pick. GSX either patches the loaded
    /// airport to the destination, or — 2026-10-10 EGCC, attempts 2 and 3 — answers at once
    /// with its own "Select Position at EGCC/Manchester" page and reports the airport only
    /// minutes later (both picks were logged GsxNoResponse after 20 s with the right page on
    /// screen). That page names the destination, so it is the pick having worked; the
    /// gate.select that follows is GSX's answer to it (ticket t-20261010-0726).
    /// </summary>
    internal static bool AirportPickVerified(GsxStateMirror mirror, string destination)
    {
        ArgumentNullException.ThrowIfNull(mirror);
        if (string.Equals(mirror.AirportIcao, destination, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var title = mirror.MenuShown ? mirror.Menu?.Title : null;
        return title is not null
            && title.StartsWith(PositionSelectTitle, StringComparison.OrdinalIgnoreCase)
            && title.Contains(destination, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> PickAirportInFlightAsync(string destination)
    {
        var menuWasShown = _api.Mirror.MenuShown;
        var rootMenu = new GsxMenuIntent
        {
            Name = "gsx menu (in-flight root)",
            TitlePrefixes = [""],
            EntryPattern = new Regex("^select airport", RegexOptions.IgnoreCase),
            Verify = mirror => mirror.MenuShown
                && mirror.Menu?.Title.StartsWith(AirportSelectTitle, StringComparison.OrdinalIgnoreCase) == true,
        };
        var airportPick = new GsxMenuIntent
        {
            Name = "in-flight airport pick",
            TitlePrefixes = [AirportSelectTitle],
            EntryPattern = new Regex($@"\b{Regex.Escape(destination)}\b", RegexOptions.IgnoreCase),
            ParentMenu = rootMenu,
            Verify = mirror => AirportPickVerified(mirror, destination),
            VerifyTimeout = AirportLoadTimeout,
        };

        var result = await _executor.ExecuteAsync(airportPick).ConfigureAwait(false);
        _eventLog.Record("gsx-gate-airport-pick", new
        {
            destination,
            outcome = result.Outcome.ToString(),
            detail = result.Detail,
            attempt = _airportPickAttempts,
            entries = _api.Mirror.MenuShown ? _api.Mirror.Menu?.Entries : null,
        });

        if (result.Succeeded)
        {
            _logger.LogInformation("GSX loaded {Destination} from the in-flight airport pick: {Detail}", destination, result.Detail);
            return true;
        }

        // The menu entries are the evidence for the next flight: which page came up and
        // what its rows looked like when the ICAO was not found.
        _logger.LogInformation(
            "In-flight airport pick for {Destination} not completed ({Outcome}): {Detail}; page '{Title}' rows [{Rows}]",
            destination,
            result.Outcome,
            result.Detail,
            _api.Mirror.Menu?.Title,
            string.Join(" | ", _api.Mirror.Menu?.Entries ?? []));

        if (!menuWasShown && _api.Mirror.MenuShown)
        {
            _ = await _api.SendCommandAsync("menu.close", null).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>The retry ladder — at most ONE auto-retry total per user request. The token
    /// goes out as typed: GSX resolves it the way its gate search does (exact bglName → exact
    /// uiGateName → full uiName → suffix on uiGateName), so "545R" finds "Stand 545R". The one
    /// not_found fallback is the parking NUMBER as an integer — the fourth identity the
    /// Remote API accepts and the only one never tried before 2026-09-21.</summary>
    private async Task DispatchLadderAsync(string requested)
    {
        var menuWasShown = _api.Mirror.MenuShown;

        // 2026-10-04 (EFHK W40): resolve the token by SCENERY IDENTITY first. The GSX profile
        // and the sim's parking list know that "W40" is GATE_W number 40 — and that GSX's
        // template prints it as "Gate 40" — so the number goes out as an integer, the identity
        // GSX's display template can never hide. The typed token stays the last rung.
        var resolved = await ResolveFromCatalogueAsync(requested).ConfigureAwait(false);
        GsxCommandResult result;
        if (resolved is not null)
        {
            result = await SendResolvedLadderAsync(requested, resolved).ConfigureAwait(false);
            if (!result.Ok && result.Code is "not_found" or "ambiguous")
            {
                result = await SendSelectAsync(JsonValue.Create(requested), revokeServices: false, force: false).ConfigureAwait(false);
            }
        }
        else
        {
            result = await SendSelectAsync(JsonValue.Create(requested), revokeServices: false, force: false).ConfigureAwait(false);
        }

        if (!result.Ok && !_retriedOnce)
        {
            switch (result.Code)
            {
                case "not_found":
                    var number = GsxGateResolver.NumberFallback(_api.Mirror.Parkings, requested);
                    if (number is { } parkingNumber)
                    {
                        _retriedOnce = true;
                        _logger.LogInformation(
                            "Gate {Gate} not found by name; retrying with parking number {Number}",
                            requested, parkingNumber);
                        _eventLog.Record("gsx-gate-number-fallback", new { gate = requested, number = parkingNumber });
                        result = await SendSelectAsync(JsonValue.Create(parkingNumber), revokeServices: false, force: false).ConfigureAwait(false);
                    }
                    break;

                case "ambiguous":
                    var candidates = ParseCandidates(result.Error);
                    var candidate = GsxGateResolver.PickUniqueCandidate(candidates, requested);
                    if (candidate?.ResendToken is { Length: > 0 } token)
                    {
                        _retriedOnce = true;
                        _logger.LogInformation("Gate {Gate} ambiguous; retrying with candidate {Token}", requested, token);
                        result = await SendSelectAsync(JsonValue.Create(token), revokeServices: false, force: false).ConfigureAwait(false);
                    }
                    else
                    {
                        Fail($"ambiguous — no unique candidate among [{string.Join(", ", candidates.Select(c => c.ResendToken))}]");
                        return;
                    }
                    break;

                case "services_active":
                    _retriedOnce = true;
                    _logger.LogInformation("Gate {Gate} has active services; retrying with revoke", requested);
                    result = await SendSelectAsync(JsonValue.Create(requested), revokeServices: true, force: false).ConfigureAwait(false);
                    break;

                case "assigned_to_other":
                    _retriedOnce = true;
                    _logger.LogInformation("Gate {Gate} occupied; retrying with force", requested);
                    result = await SendSelectAsync(JsonValue.Create(requested), revokeServices: false, force: true).ConfigureAwait(false);
                    break;
            }
        }

        HandleFinalResult(requested, result);

        // The in-flight pick leaves GSX's gate page on screen; the pilot did not open it.
        if (!menuWasShown && _api.Mirror.MenuShown)
        {
            _ = await _api.SendCommandAsync("menu.close", null).ConfigureAwait(false);
        }
    }

    /// <summary>The catalogue's view of the requested gate at the airport GSX has loaded (or the
    /// FMS destination while it has not). Null when the catalogue knows nothing or the token
    /// matches no stand — then the typed token goes out alone, as before.</summary>
    private async Task<ParkingMatch?> ResolveFromCatalogueAsync(string requested)
    {
        if (_parkings is null)
        {
            return null;
        }

        var icao = _api.Mirror.AirportIcao ?? _destination.Value;
        if (string.IsNullOrWhiteSpace(icao))
        {
            return null;
        }

        try
        {
            var catalogue = await _parkings.GetAsync(icao).ConfigureAwait(false);
            var match = ParkingTokenResolver.Resolve(catalogue, requested);
            if (match is null)
            {
                if (catalogue is not null)
                {
                    _logger.LogInformation(
                        "Gate {Gate} matches none of the {Count} stands the profile/scenery know at {Icao}; sending the token as typed",
                        requested, catalogue.Parkings.Count, icao);
                }

                return null;
            }

            _logger.LogInformation(
                "Gate {Gate} resolved at {Icao} to {Identity} (GSX name '{GsxName}', {Confidence}, sources {Sources}); will send {Tokens}",
                requested, icao, match.Parking.Identity.DefaultDisplayName, match.Parking.DisplayName,
                match.Confidence, match.Parking.Sources, string.Join(" → ", match.GsxTokens));
            _eventLog.Record("gsx-gate-resolved", new
            {
                gate = requested,
                airport = icao,
                identity = match.Parking.Identity.DefaultDisplayName,
                gsxName = match.Parking.GsxUiName ?? match.Parking.DisplayName,
                confidence = match.Confidence.ToString(),
                sources = match.Parking.Sources.ToString(),
                tokens = match.GsxTokens.Select(t => t.ToString()).ToList(),
                alternatives = match.Alternatives.Select(a => a.Identity.DefaultDisplayName).ToList(),
            });
            return match;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Parking catalogue lookup failed for {Icao}; sending the token as typed", icao);
            return null;
        }
    }

    /// <summary>Walks the resolved identities (number → GSX gate name → full name → default
    /// name). <c>not_found</c> moves to the next rung; <c>ambiguous</c> picks the candidate
    /// whose bglName/uiName names the resolved stand; any other outcome returns at once for
    /// the caller's revoke/force handling.</summary>
    private async Task<GsxCommandResult> SendResolvedLadderAsync(string requested, ParkingMatch match)
    {
        var result = GsxCommandResult.Synthetic("not_found");
        foreach (var token in match.GsxTokens)
        {
            JsonNode node = token is int number ? JsonValue.Create(number) : JsonValue.Create(token.ToString()!);
            result = await SendSelectAsync(node, revokeServices: false, force: false).ConfigureAwait(false);
            if (result.Ok || result.Code is not ("not_found" or "ambiguous"))
            {
                return result;
            }

            if (result.Code == "ambiguous")
            {
                var candidates = ParseCandidates(result.Error);
                var expectedBgl = ParkingText.Normalize(match.Parking.Identity.DefaultDisplayName);
                var expectedUi = ParkingText.Normalize(match.Parking.GsxUiName);
                var candidate = candidates.FirstOrDefault(c =>
                    ParkingText.Normalize(c.BglName) == expectedBgl
                    || (expectedUi.Length > 0 && ParkingText.Normalize(c.UiName) == expectedUi));
                if (candidate?.ResendToken is { Length: > 0 } resend)
                {
                    _logger.LogInformation("Gate {Gate}: {Token} ambiguous; picking candidate {Candidate}", requested, token, resend);
                    result = await SendSelectAsync(JsonValue.Create(resend), revokeServices: false, force: false).ConfigureAwait(false);
                    if (result.Ok || result.Code is not ("not_found" or "ambiguous"))
                    {
                        return result;
                    }
                }
            }
        }

        return result;
    }

    private void HandleFinalResult(string requested, GsxCommandResult result)
    {
        if (result.Ok || result.Code is "ok" or "prepared" or "already_selected" or "already_parked")
        {
            _assigned = AssignedFrom(requested, result.Payload);
            _eventLog.Record("gsx-gate-assigned", new { gate = requested, code = result.Code, payload = result.Payload?.ToJsonString() });
            SetStatus(GsxGateRequestStatus.Assigned, $"assigned ({result.Code}); awaiting confirmation");
            StartConfirmationWindow();
            CheckReadback();
            return;
        }

        switch (result.Code)
        {
            case "no_airport" or "gsx_not_running" or "not_connected":
                // Not failures — stay armed; state changes re-dispatch.
                SetStatus(GsxGateRequestStatus.Armed, $"waiting ({result.Code})");
                return;

            case "not_found":
                // Issue #156 (EFHK W40, 2026-10-04): four refusals and the log said only "gate
                // not found" — whether the mirror had EFHK's parkings at all was unknowable.
                // Record what the mirror held so the next refusal is conclusive.
                var mirror = _api.Mirror;
                var nearest = GsxGateResolver.NearestNames(mirror.Parkings, requested);
                var sample = GsxGateResolver.SampleNames(mirror.Parkings, 8);
                _logger.LogWarning(
                    "Gate {Gate} not found at GSX airport {Airport} ({Count} parkings mirrored; nearest [{Nearest}]; sample [{Sample}])",
                    requested, mirror.AirportIcao ?? "none", mirror.Parkings.Count, string.Join(", ", nearest), string.Join(", ", sample));
                _eventLog.Record("gsx-gate-not-found", new
                {
                    gate = requested,
                    airport = mirror.AirportIcao,
                    parkings = mirror.Parkings.Count,
                    nearest,
                    sample,
                });
                Fail(nearest.Count > 0
                    ? $"gate not found; nearest: {string.Join(", ", nearest)}"
                    : mirror.Parkings.Count == 0
                        ? $"gate not found — GSX sent no parking list for {mirror.AirportIcao ?? "the airport"}"
                        : $"gate not found among {mirror.Parkings.Count} parkings at {mirror.AirportIcao}");
                return;

            default:
                Fail($"gate.select failed ({result.Code})");
                return;
        }
    }

    /// <summary>GSX's own identity for the gate it accepted, from the gate.select payload:
    /// the documented <c>payload.gate</c> ref, else a ref at the payload root. First read
    /// 2026-10-04 (LKPR, integer token 29 → uiName "Gate C 29").</summary>
    internal static GsxAssignedGate AssignedFrom(string requested, JsonObject? payload)
    {
        var node = payload?["gate"] as JsonObject ?? payload;
        var reference = node is null ? null : GsxGateRef.Parse(node);
        return new GsxAssignedGate(requested, reference?.UiName ?? reference?.Gate, reference?.Number);
    }

    private Task<GsxCommandResult> SendSelectAsync(JsonNode? gate, bool revokeServices, bool force)
        => _api.SendCommandAsync("gate.select", new JsonObject
        {
            ["gate"] = gate,
            ["revokeServices"] = revokeServices,
            ["force"] = force,
        });

    private static List<GsxGateRef> ParseCandidates(JsonObject? error)
    {
        var list = new List<GsxGateRef>();
        if (error?["candidates"] is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is JsonObject candidate)
                {
                    list.Add(GsxGateRef.Parse(candidate));
                }
            }
        }
        return list;
    }

    private void StartConfirmationWindow()
    {
        _confirmationTimer?.Dispose();
        _confirmationTimer = new Timer(_ =>
        {
            if (Status == GsxGateRequestStatus.Assigned)
            {
                // Window expiry is not a failure — the assignment stands, just unconfirmed.
                SetStatus(GsxGateRequestStatus.AssignedUnconfirmed, "readback window expired");
            }
        }, null, ConfirmationWindow, Timeout.InfiniteTimeSpan);
    }

    private void CheckReadback()
    {
        if (Status != GsxGateRequestStatus.Assigned)
        {
            return;
        }

        var formatted = GsxGateResolver.FormatReadback(
            _gateName.Value,
            _gateNumber.Value,
            _gateSuffix.Value);
        if (formatted is null)
        {
            return;
        }

        string? requested;
        lock (_gate)
        {
            requested = _requestedGate;
        }

        if (requested is not null
            && GsxGateResolver.Normalize(formatted) == GsxGateResolver.Normalize(requested))
        {
            _confirmationTimer?.Dispose();
            _confirmationTimer = null;
            _eventLog.Record("gsx-gate-confirmed", new { gate = requested, readback = formatted });
            SetStatus(GsxGateRequestStatus.Confirmed, $"confirmed as {formatted}");
        }
    }

    private void Fail(string detail)
    {
        lock (_gate)
        {
            _lastFailedGate = _requestedGate;
            _lastFailedAtTicks = Environment.TickCount64;
        }
        _eventLog.Record("gsx-gate-failed", new { gate = _requestedGate, detail });
        SetStatus(GsxGateRequestStatus.Failed, detail);
    }

    private void SetStatus(GsxGateRequestStatus status, string detail)
    {
        Status = status;
        StatusDetail = detail;
        _logger.LogInformation("Gate request {Gate}: {Status} — {Detail}", _requestedGate ?? "<none>", status, detail);
        Changed?.Invoke();
    }

    private void OnStateChanged(GsxReadiness readiness) => _ = TryDispatchAsync();

    private void OnDataChanged(object? sender, EventArgs e) => _ = TryDispatchAsync();

    private void OnPhaseChanged(object? sender, FlightPhaseChangedEventArgs e) => _ = TryDispatchAsync();

    /// <summary>
    /// Issue #156 fallback: GSX raised "Select Position at …" itself (landing roll, or parked
    /// with no gate recognised) while a gate request stands unfulfilled. Answer it from the
    /// page's own rows — the group whose range covers the token, then the position that names
    /// it — through the intent executor, so the pick is text-resolved and the menu stays with
    /// the pilot on any miss. At most two attempts per request; never while the executor is
    /// already driving that menu (our own reposition step opens the same page).
    /// </summary>
    internal async Task TryAnswerPositionMenuAsync()
    {
        if (_options?.CurrentValue.AnswerPositionMenuWithArrivalGate != true)
        {
            return;
        }

        string requested;
        var mirror = _api.Mirror;
        lock (_gate)
        {
            if (_requestedGate is null || _answeringPositionMenu || _positionMenuAttempts >= PositionMenuMaxAttempts
                || Status is GsxGateRequestStatus.Assigned or GsxGateRequestStatus.Confirmed or GsxGateRequestStatus.AssignedUnconfirmed
                || !mirror.MenuShown
                || mirror.Menu?.Title.StartsWith(PositionSelectTitle, StringComparison.OrdinalIgnoreCase) != true
                || _executor.IsDriving(mirror.Menu.Title))
            {
                return;
            }

            _answeringPositionMenu = true;
            _positionMenuAttempts++;
            requested = _requestedGate;
        }

        try
        {
            var page = mirror.Menu!;
            var pick = GsxPositionMenuPlanner.Pick(page.Entries, requested);
            if (pick is null)
            {
                _logger.LogInformation(
                    "GSX position menu '{Title}' has no single row for {Gate}; left for the pilot. Rows [{Rows}]",
                    page.Title, requested, string.Join(" | ", page.Entries));
                _eventLog.Record("gsx-gate-position-menu", new { gate = requested, outcome = "NoRow", title = page.Title, entries = page.Entries });
                return;
            }

            SetStatus(GsxGateRequestStatus.Dispatching, $"answering GSX's position menu with {requested}");
            var facility = new GsxMenuIntent
            {
                Name = "position menu facility",
                TitlePrefixes = [PositionSelectTitle],
                EntryPattern = new Regex("^" + Regex.Escape(pick.Entry) + "$", RegexOptions.IgnoreCase),
            };
            var intent = pick.IsPosition
                ? facility with { Name = "position menu pick" }
                : new GsxMenuIntent
                {
                    Name = "position menu pick",
                    TitlePrefixes = [PositionsPageTitle],
                    EntryPattern = new Regex($@"(?<![A-Z0-9]){Regex.Escape(requested)}(?![A-Z0-9])", RegexOptions.IgnoreCase),
                    ParentMenu = facility,
                };

            var result = await _executor.ExecuteAsync(intent).ConfigureAwait(false);
            _eventLog.Record("gsx-gate-position-menu", new
            {
                gate = requested,
                outcome = result.Outcome.ToString(),
                detail = result.Detail,
                facility = pick.Entry,
                direct = pick.IsPosition,
                attempt = _positionMenuAttempts,
                entries = _api.Mirror.MenuShown ? _api.Mirror.Menu?.Entries : null,
            });

            if (result.Succeeded)
            {
                _logger.LogInformation("Arrival gate {Gate} picked in GSX's position menu via '{Facility}'", requested, pick.Entry);
                _assigned = new GsxAssignedGate(requested, GsxName: null, Number: null);
                SetStatus(GsxGateRequestStatus.Assigned, "picked in GSX's position menu; awaiting confirmation");
                StartConfirmationWindow();
                CheckReadback();
            }
            else
            {
                _logger.LogInformation(
                    "Answering GSX's position menu for {Gate} did not complete ({Outcome}): {Detail}; page '{Title}' rows [{Rows}]",
                    requested, result.Outcome, result.Detail, _api.Mirror.Menu?.Title, string.Join(" | ", _api.Mirror.Menu?.Entries ?? []));
                SetStatus(GsxGateRequestStatus.Failed, $"GSX position menu: {result.Detail} — pick the gate in the GSX menu");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Answering GSX's position menu failed unexpectedly");
        }
        finally
        {
            lock (_gate)
            {
                _answeringPositionMenu = false;
            }
        }
    }

    private void OnMirrorUpdated(string key)
    {
        if (string.Equals(key, "menu", StringComparison.OrdinalIgnoreCase))
        {
            _ = TryAnswerPositionMenuAsync();
            return;
        }

        // Live GSX 4 pushes the loaded airport as the top-level /airport key (the mirror's
        // preferred source); handlerData is the fallback shape. Both must re-evaluate — the
        // pre-2026-09-21 wiring listened to handlerData only.
        if (string.Equals(key, "handlerData", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "airport", StringComparison.OrdinalIgnoreCase))
        {
            _ = TryDispatchAsync();
        }
    }

    private void OnSidChanged(string? oldSid, string? newSid)
    {
        // Engine restart drops the prepared gate: re-arm the last request.
        bool rearm;
        lock (_gate)
        {
            rearm = _requestedGate is not null;
            _retriedOnce = false;
            _tooLateReported = false;
            _airportPickAttempts = 0;
            _lastAirportPickAtUtc = null;
        }

        if (rearm && Status is GsxGateRequestStatus.Assigned or GsxGateRequestStatus.Confirmed
            or GsxGateRequestStatus.AssignedUnconfirmed or GsxGateRequestStatus.Dispatching)
        {
            SetStatus(GsxGateRequestStatus.Armed, "re-armed after GSX engine restart");
            _ = TryDispatchAsync();
        }
    }

    private void OnReadbackChanged(object? sender, EventArgs e) => CheckReadback();
}
