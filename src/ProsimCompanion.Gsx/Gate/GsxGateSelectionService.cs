using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Airports.Parking;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
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
/// destination through its own "Select airport" menu — walking its pages (issue #75) — then
/// <c>gate.select</c> goes out with the stand's scenery identity first and the token as typed
/// last. On the ground the send happens the moment GSX loads the airport itself, while still
/// rolling; once parked it is too late by GSX's own rule. A <c>no_airport</c> answer is not a
/// failure: GSX is still loading the destination and the next airport patch re-dispatches.
/// When GSX answers <c>ambiguous</c> with the stand's suffix variants (W36 → 36A / 36B,
/// issue #156) the pilot's suffix, a lone jetway, the pilot's answer (web buttons / the FO)
/// or the taxi-in default decides. Success is provisional until the SetGate_* LVAR readback
/// matches (60 s window, checked immediately too). A Couatl restart re-arms the last request.
/// </summary>
public sealed class GsxGateSelectionService : Core.State.IGsxGateControl, IGsxAssignedGateSource, IDisposable
{
    private static readonly TimeSpan ConfirmationWindow = TimeSpan.FromSeconds(60);

    /// <summary>Minimum quiet time before an identical failed gate.select may be re-dispatched
    /// (issue #75: mirror updates arrive about once a second and re-invoke the dispatcher — a
    /// failed request re-fired 947 ms after its own failure, twice within the same second).</summary>
    private static readonly TimeSpan FailedRetryBackoff = TimeSpan.FromSeconds(10);

    /// <summary>Quiet time after a <c>no_airport</c> answer: GSX is loading the destination and
    /// every mirror patch would otherwise re-send (2026-10-10: three refusals a minute apart
    /// before the airport patch landed).</summary>
    private static readonly TimeSpan NoAirportBackoff = TimeSpan.FromSeconds(60);

    /// <summary>GSX's in-flight airport list page (Prosim2GSX GsxParkingSelector archaeology:
    /// the title is exactly this; rows carry the ICAO).</summary>
    internal const string AirportSelectTitle = "Select airport";

    /// <summary>The destination context arrives as a handlerData/airport patch after the pick;
    /// GSX loads the airport in the background, so allow well beyond the 5 s default.</summary>
    private static readonly TimeSpan AirportLoadTimeout = TimeSpan.FromSeconds(20);

    /// <summary>How long a <c>menu.search</c> on the airport list may take to filter the rows.</summary>
    private static readonly TimeSpan SearchSettle = TimeSpan.FromSeconds(3);

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
    private DateTimeOffset? _lastAirportWalkAtUtc;
    private DateTimeOffset? _lastNoAirportAtUtc;
    private int _airportPickAttempts;
    private int _positionMenuAttempts;
    private bool _answeringPositionMenu;
    private IReadOnlyList<GsxGateRef>? _pendingVariants;
    private GsxAssignedGate? _assigned;
    private readonly IOptionsMonitor<GsxOptions>? _options;
    private readonly IAirportParkingCatalog? _parkings;
    private readonly GateChoiceStore? _gateChoice;
    private readonly IDisposable? _gateChoiceSubscription;
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
        IAirportParkingCatalog? parkings = null,
        GateChoiceStore? gateChoice = null)
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
        _gateChoice = gateChoice;

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
        _gateChoiceSubscription = _gateChoice?.Observe(OnGateChoiceChanged);
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
            _lastAirportWalkAtUtc = null;
            _lastNoAirportAtUtc = null;
            _positionMenuAttempts = 0;
            _pendingVariants = null;
        }
        _gateChoice?.ClearQuestion();
        SetStatus(GsxGateRequestStatus.Armed, $"armed for {gate}");
        _ = TryDispatchAsync();
    }

    public void Cancel()
    {
        lock (_gate)
        {
            _requestedGate = null;
            _assigned = null;
            _pendingVariants = null;
        }
        _gateChoice?.ClearQuestion();
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
        _gateChoiceSubscription?.Dispose();
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
        int attempt;
        lock (_gate)
        {
            if (_requestedGate is null || _dispatching || _pendingVariants is not null
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

            var now = DateTimeOffset.UtcNow;
            var view = _flightState.Snapshot();
            destination = _destination.Value?.Trim() ?? "";
            step = GateDispatchPlanner.Decide(new GateDispatchInputs(
                _api.Readiness == GsxReadiness.Ready,
                _api.Mirror.AirportIcao,
                destination,
                view.Phase,
                view.Data,
                now,
                _lastAirportPickAtUtc,
                _airportPickAttempts,
                _lastAirportWalkAtUtc));

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
                    _lastAirportPickAtUtc = now;
                    break;

                case GateDispatchStep.Send:
                    // GSX said no_airport a moment ago: the destination is loading, the next
                    // airport patch (not the next mirror tick) is the moment to send again.
                    if (_lastNoAirportAtUtc is { } refused && now - refused < NoAirportBackoff)
                    {
                        return;
                    }
                    break;
            }

            _dispatching = true;
            requested = _requestedGate;
            attempt = _airportPickAttempts + 1;
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
                    SetStatus(GsxGateRequestStatus.Armed, $"loading {destination} in GSX (in-flight airport pick, attempt {attempt})");
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
    /// The observable effect of the in-flight airport row pick. GSX either patches the loaded
    /// airport to the destination, or — 2026-10-10 EGCC, attempts 2 and 3 — answers at once
    /// with its own "Select Position at EGCC/Manchester" page and reports the airport only
    /// minutes later (both picks were logged GsxNoResponse after 20 s with the right page on
    /// screen). That page names the destination, so it is the pick having worked; the
    /// gate.select that follows may still answer <c>no_airport</c> while GSX loads — that
    /// answer keeps the request armed (ticket t-20261010-0726, issue #75).
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

    /// <summary>
    /// The in-flight airport pick (the Handler Scripts guide's onSelectGateInFlight moment):
    /// GSX menu → "Select airport" page → the row carrying the destination ICAO, on ANY page
    /// of the list. Page 1 holds the PLANNED airport and the nearest fields only (2026-10-11:
    /// "EGCC Manchester at 69.47 nm [PLANNED]" plus eight fields within 13 nm — EFHK was never
    /// on it, three times), so the walk turns "Next Page ▶" up to <c>gsx.airportPickMenuPages</c>
    /// times and uses GSX's own search row first when the page offers one. The row is matched
    /// by ICAO text, never by ordinal (§5 rule; the predecessor's row-2 fallback is
    /// deliberately not carried). A menu we opened is closed again on failure; on success the
    /// gate.select that follows closes it. Only a walk that reached the list counts toward the
    /// per-flight cap and starts the long quiet time (<see cref="GateDispatchPlanner"/>).
    /// <para>2026-10-04 EFHK→LKPR (issue #157): in flight GSX's ROOT menu is itself titled
    /// "Select airport" and lists "LKPR Ruzyne at 592.70 nm [PLANNED]" — there is no root
    /// entry to pick first. The executor runs the row pick directly when the opened page is
    /// already this one; the root intent stays for a GSX that shows a root menu first.</para>
    /// </summary>
    private async Task<bool> PickAirportInFlightAsync(string destination)
    {
        var mirror = _api.Mirror;
        var menuWasShown = mirror.MenuShown;

        // GSX is already on its own "Select Position at <destination>" page from an earlier
        // pick (leg 1, 2026-10-10: five picks reported Success on that page two minutes apart
        // and every gate.select in between answered no_airport) — the airport is loading;
        // there is nothing to pick again.
        if (AirportPickVerified(mirror, destination))
        {
            RecordAirportPick(destination, GsxIntentOutcome.Success, "GSX already shows the destination's own page — airport loading, nothing to pick", pages: 0, pagesSeen: null);
            return true;
        }

        var rootMenu = new GsxMenuIntent
        {
            Name = "gsx menu (in-flight root)",
            TitlePrefixes = [""],
            EntryPattern = new Regex("^select airport", RegexOptions.IgnoreCase),
            Verify = m => m.MenuShown
                && m.Menu?.Title.StartsWith(AirportSelectTitle, StringComparison.OrdinalIgnoreCase) == true,
        };
        var airportPage = new GsxMenuIntent
        {
            Name = "in-flight airport list",
            TitlePrefixes = [AirportSelectTitle],
            ParentMenu = rootMenu,
        };

        var reached = await _executor.ExecuteAsync(airportPage).ConfigureAwait(false);
        if (!reached.Succeeded)
        {
            RecordAirportPick(destination, reached.Outcome, reached.Detail, pages: 0, pagesSeen: null);
            LogPickNotCompleted(destination, reached);
            await CloseIfWeOpenedAsync(menuWasShown).ConfigureAwait(false);
            return false;
        }

        // A real page walk: the attempt that counts and paces the next one.
        lock (_gate)
        {
            _airportPickAttempts++;
            _lastAirportWalkAtUtc = DateTimeOffset.UtcNow;
        }

        var rowPattern = new Regex($@"\b{Regex.Escape(destination)}\b", RegexOptions.IgnoreCase);
        var maxPages = Math.Max(1, _options?.CurrentValue.AirportPickMenuPages ?? 4);
        var pagesSeen = new List<string>();
        var searched = false;
        for (var page = 0; page < maxPages; page++)
        {
            var menu = mirror.MenuShown ? mirror.Menu : null;
            if (menu is null || !menu.Title.StartsWith(AirportSelectTitle, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            pagesSeen.Add(GsxMenuPaging.DescribePage(menu, pagesSeen.Count + 1));
            if (menu.Entries.Any(rowPattern.IsMatch))
            {
                var airportPick = new GsxMenuIntent
                {
                    Name = "in-flight airport pick",
                    TitlePrefixes = [AirportSelectTitle],
                    EntryPattern = rowPattern,
                    Verify = m => AirportPickVerified(m, destination),
                    VerifyTimeout = AirportLoadTimeout,
                };
                var result = await _executor.ExecuteAsync(airportPick).ConfigureAwait(false);
                RecordAirportPick(destination, result.Outcome, result.Detail, pagesSeen.Count, pagesSeen);
                if (result.Succeeded)
                {
                    _logger.LogInformation("GSX loaded {Destination} from the in-flight airport pick (page {Page}): {Detail}", destination, pagesSeen.Count, result.Detail);
                    return true;
                }

                LogPickNotCompleted(destination, result);
                await CloseIfWeOpenedAsync(menuWasShown).ConfigureAwait(false);
                return false;
            }

            // GSX's own filter beats paging when the page offers it (manual §8.11, menu.search).
            if (!searched && menu.Entries.Any(e => e.TrimStart().StartsWith("Search", StringComparison.OrdinalIgnoreCase)))
            {
                searched = true;
                var search = await _api.SendCommandAsync("menu.search", new JsonObject { ["text"] = destination }).ConfigureAwait(false);
                _logger.LogInformation("In-flight airport list offers a search row; menu.search '{Destination}' -> {Code}", destination, search.Code);
                if (search.Ok && await WaitForRowAsync(rowPattern, SearchSettle).ConfigureAwait(false))
                {
                    page--; // re-scan the filtered page; `searched` keeps this to one pass
                    continue;
                }
            }

            if (page + 1 >= maxPages)
            {
                break; // the bound: never turn a page the walk would not read
            }

            var turned = await GsxMenuPaging.TurnPageAsync(_executor, "in-flight airport list next page", [AirportSelectTitle], menu.Entries).ConfigureAwait(false);
            if (turned is null)
            {
                break; // the last page
            }

            if (!turned.Succeeded)
            {
                RecordAirportPick(destination, turned.Outcome, $"page {pagesSeen.Count + 1}: {turned.Detail}", pagesSeen.Count, pagesSeen);
                LogPickNotCompleted(destination, turned);
                await CloseIfWeOpenedAsync(menuWasShown).ConfigureAwait(false);
                return false;
            }
        }

        var notFound = new GsxIntentResult(
            GsxIntentOutcome.ItemNotAvailable,
            $"no row for {destination} on {pagesSeen.Count} page(s) of '{AirportSelectTitle}'");
        RecordAirportPick(destination, notFound.Outcome, notFound.Detail, pagesSeen.Count, pagesSeen);
        LogPickNotCompleted(destination, notFound);
        await CloseIfWeOpenedAsync(menuWasShown).ConfigureAwait(false);
        return false;
    }

    private async Task<bool> WaitForRowAsync(Regex rowPattern, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var menu = _api.Mirror.MenuShown ? _api.Mirror.Menu : null;
            if (menu is not null && menu.Entries.Any(rowPattern.IsMatch))
            {
                return true;
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        return false;
    }

    private void RecordAirportPick(string destination, GsxIntentOutcome outcome, string detail, int pages, IReadOnlyList<string>? pagesSeen)
        => _eventLog.Record("gsx-gate-airport-pick", new
        {
            destination,
            outcome = outcome.ToString(),
            detail,
            attempt = _airportPickAttempts,
            pages,
            entries = _api.Mirror.MenuShown ? _api.Mirror.Menu?.Entries : null,
            pagesSeen,
        });

    /// <summary>The menu pages are the evidence for the next flight: which page came up and
    /// what its rows looked like when the ICAO was not found.</summary>
    private void LogPickNotCompleted(string destination, GsxIntentResult result)
        => _logger.LogInformation(
            "In-flight airport pick for {Destination} not completed ({Outcome}): {Detail}; page '{Title}' rows [{Rows}]",
            destination,
            result.Outcome,
            result.Detail,
            _api.Mirror.Menu?.Title,
            string.Join(" | ", _api.Mirror.Menu?.Entries ?? []));

    /// <summary>The in-flight pick leaves GSX's page on screen; the pilot did not open it.</summary>
    private async Task CloseIfWeOpenedAsync(bool menuWasShown)
    {
        if (!menuWasShown && _api.Mirror.MenuShown)
        {
            _ = await _api.SendCommandAsync("menu.close", null).ConfigureAwait(false);
        }
    }

    /// <summary>The retry ladder — at most ONE auto-retry total per user request. The stand's
    /// scenery identity goes first (number as an integer, then GSX's names), the token as
    /// typed last: GSX resolves it the way its gate search does (exact bglName → exact
    /// uiGateName → full uiName → suffix on uiGateName). An <c>ambiguous</c> answer whose
    /// candidates are the gate's suffix variants is settled by <see cref="ChooseSuffixVariantAsync"/>;
    /// the one not_found fallback is the parking NUMBER as an integer.</summary>
    private async Task DispatchLadderAsync(string requested)
    {
        var menuWasShown = _api.Mirror.MenuShown;

        // 2026-10-04 (EFHK W40): resolve the token by SCENERY IDENTITY first. The GSX profile
        // and the sim's parking list know that "W40" is GATE_W number 40 — and that GSX's
        // template prints it as "Gate 40" — so the number goes out as an integer, the identity
        // GSX's display template can never hide. The typed token stays the last rung.
        var resolved = await ResolveFromCatalogueAsync(requested).ConfigureAwait(false);
        var result = resolved is not null
            ? await SendResolvedLadderAsync(requested, resolved).ConfigureAwait(false)
            : await SendSelectAsync(JsonValue.Create(requested), revokeServices: false, force: false).ConfigureAwait(false);

        // Issue #156 (EFHK W36, 2026-10-11): the number answers with the scenery's suffix
        // variants (" Gate 36A" / " Gate 36B"). None of the name rungs can find "36" — they
        // all answered not_found that day — so the choice is made here, not after them.
        if (!result.Ok && result.Code == "ambiguous")
        {
            var candidates = ParseCandidates(result.Error);
            var variants = GsxGateResolver.SuffixVariants(candidates, requested);
            if (variants is not null)
            {
                var chosen = await ChooseSuffixVariantAsync(requested, variants).ConfigureAwait(false);
                if (chosen is null)
                {
                    await CloseIfWeOpenedAsync(menuWasShown).ConfigureAwait(false);
                    return; // the pilot's answer (or the taxi-in default) resumes the dispatch
                }

                _retriedOnce = true;
                result = await SendSelectAsync(JsonValue.Create(chosen.ResendToken), revokeServices: false, force: false).ConfigureAwait(false);
                HandleFinalResult(requested, result);
                await CloseIfWeOpenedAsync(menuWasShown).ConfigureAwait(false);
                return;
            }
        }

        if (resolved is not null && !result.Ok && result.Code is "not_found" or "ambiguous")
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
                        await CloseIfWeOpenedAsync(menuWasShown).ConfigureAwait(false);
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
        await CloseIfWeOpenedAsync(menuWasShown).ConfigureAwait(false);
    }

    /// <summary>
    /// Which suffix variant to send for a request GSX split (issue #156): the suffix the pilot
    /// or ATC typed ("36A"); else the one stand of the variants with a jetway / Safedock in the
    /// catalogue (marked Likely); else — with <c>gsx.askPilotOnAmbiguousGate</c> — the pilot's
    /// answer through the <see cref="GateChoiceStore"/> (web buttons, the FO's question), the
    /// first variant at taxi-in without one; else the first variant at once. Null while the
    /// question stands.
    /// </summary>
    private async Task<GsxGateRef?> ChooseSuffixVariantAsync(string requested, IReadOnlyList<GsxGateRef> variants)
    {
        var labels = variants.Select(GsxGateResolver.CandidateLabel).ToList();

        if (GsxGateResolver.PickBySuffix(variants, requested) is { } bySuffix)
        {
            RecordSuffixChoice(requested, labels, "pilotSuffix", GsxGateResolver.CandidateLabel(bySuffix), "the request carries the suffix");
            return bySuffix;
        }

        var details = await DescribeVariantsAsync(variants).ConfigureAwait(false);
        var withJetway = Enumerable.Range(0, variants.Count).Where(i => details[i] is not null).ToList();
        if (withJetway.Count == 1)
        {
            var index = withJetway[0];
            RecordSuffixChoice(requested, labels, "jetway", labels[index], $"Likely — the only variant with a {details[index]}");
            return variants[index];
        }

        if (_options?.CurrentValue.AskPilotOnAmbiguousGate != false && _gateChoice is not null)
        {
            var candidates = variants
                .Select((v, i) => new GateCandidate(labels[i], v.ResendToken ?? labels[i], details[i]))
                .ToList();
            var prompt = $"{requested} is {JoinOr(labels)} in this scenery — which?";
            lock (_gate)
            {
                _pendingVariants = variants;
            }

            _gateChoice.Ask(requested, candidates, prompt);
            RecordSuffixChoice(requested, labels, "asked", null, "waiting for the pilot; the first variant goes at taxi-in without an answer");
            SetStatus(
                GsxGateRequestStatus.Armed,
                $"{requested} is {JoinOr(labels)} here — pick one on the OFP page or the Status card, or tell the FO; {labels[0]} at taxi-in otherwise");
            return null;
        }

        RecordSuffixChoice(requested, labels, "default", labels[0], "first variant — nothing else decides");
        return variants[0];
    }

    /// <summary>"jetway" / "Safedock" per variant from the parking catalogue, null when the
    /// stand has neither (or the catalogue knows nothing).</summary>
    private async Task<string?[]> DescribeVariantsAsync(IReadOnlyList<GsxGateRef> variants)
    {
        var details = new string?[variants.Count];
        var icao = _api.Mirror.AirportIcao ?? _destination.Value;
        if (_parkings is null || string.IsNullOrWhiteSpace(icao))
        {
            return details;
        }

        try
        {
            var catalogue = await _parkings.GetAsync(icao).ConfigureAwait(false);
            if (catalogue is null)
            {
                return details;
            }

            for (var i = 0; i < variants.Count; i++)
            {
                if (GsxGateResolver.CandidateParts(variants[i]) is not { } parts)
                {
                    continue;
                }

                var stand = catalogue.Parkings.FirstOrDefault(p =>
                    p.Identity.Number == parts.Number
                    && string.Equals(p.Identity.Suffix, parts.Suffix, StringComparison.OrdinalIgnoreCase)
                    && (parts.Name is null || p.Identity.Name == parts.Name));
                if (stand is null)
                {
                    continue;
                }

                if (stand.ParkingSystem is { Length: > 0 } system
                    && (system.Contains("SafeDock", StringComparison.OrdinalIgnoreCase) || system.Contains("VDGS", StringComparison.OrdinalIgnoreCase)))
                {
                    details[i] = "Safedock";
                }
                else if (stand.HasJetway == true)
                {
                    details[i] = "jetway";
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Parking catalogue lookup for the suffix variants at {Icao} failed", icao);
        }

        return details;
    }

    private void RecordSuffixChoice(string requested, IReadOnlyList<string> labels, string decision, string? chosen, string detail)
    {
        _logger.LogInformation(
            "Gate {Gate} is {Labels} in this scenery — {Decision}{Chosen}: {Detail}",
            requested, JoinOr(labels), decision, chosen is null ? "" : $" {chosen}", detail);
        _eventLog.Record("gsx-gate-suffix-choice", new { gate = requested, candidates = labels, decision, chosen, detail });
    }

    private static string JoinOr(IReadOnlyList<string> labels)
        => labels.Count <= 1 ? string.Join("", labels) : $"{string.Join(", ", labels.Take(labels.Count - 1))} or {labels[^1]}";

    /// <summary>The pilot (web, voice) or the taxi-in default answered the suffix question.</summary>
    private void OnGateChoiceChanged(GateChoiceSnapshot snapshot)
    {
        if (snapshot.Choice is { } choice)
        {
            _ = DispatchChosenVariantAsync(choice);
        }
    }

    private async Task DispatchChosenVariantAsync(GateChoice choice)
    {
        GsxGateRef? variant;
        string requested;
        lock (_gate)
        {
            if (_dispatching || _pendingVariants is null || _requestedGate is null
                || !string.Equals(choice.Gate, _requestedGate, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            variant = _pendingVariants.FirstOrDefault(v => string.Equals(v.ResendToken, choice.Candidate.Token, StringComparison.Ordinal));
            if (variant is null)
            {
                return;
            }

            _pendingVariants = null;
            _dispatching = true;
            requested = _requestedGate;
        }

        try
        {
            RecordSuffixChoice(requested, [choice.Candidate.Label], choice.Source, choice.Candidate.Label, $"chosen by {choice.Source}");
            SetStatus(GsxGateRequestStatus.Dispatching, $"selecting {choice.Candidate.Label} for {requested} ({choice.Source})");
            _retriedOnce = true;
            var menuWasShown = _api.Mirror.MenuShown;
            var result = await SendSelectAsync(JsonValue.Create(variant.ResendToken), revokeServices: false, force: false).ConfigureAwait(false);
            HandleFinalResult(requested, result);
            await CloseIfWeOpenedAsync(menuWasShown).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sending the chosen gate variant failed unexpectedly");
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

    /// <inheritdoc />
    public bool TryAdoptFacility(string facility)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(facility);
        string requested;
        string adopted;
        lock (_gate)
        {
            if (_requestedGate is null || Status == GsxGateRequestStatus.Confirmed)
            {
                return false;
            }

            requested = _requestedGate;
            if (_assigned?.GsxName is { } assignedName
                && GsxGateResolver.FacilityNamesGate(facility, requested, assignedName))
            {
                return false; // the facility IS our assignment — nothing to adopt
            }

            if (GsxGateResolver.SuffixVariantIn(facility, requested) is not { } variant)
            {
                return false;
            }

            adopted = variant;
            _assigned = new GsxAssignedGate(requested, adopted, ParkingIdentity.TryParseSceneryName(adopted)?.Number);
            _pendingVariants = null;
        }

        _gateChoice?.ClearQuestion();
        _logger.LogInformation("GSX picked {Adopted} for our {Gate} (facility '{Facility}') — adopted as the arrival gate", adopted, requested, facility);
        _eventLog.Record("gsx-gate-adopted", new { gate = requested, facility, adopted });
        SetStatus(GsxGateRequestStatus.Assigned, $"GSX picked {adopted} for {requested}; awaiting confirmation");
        StartConfirmationWindow();
        CheckReadback();
        return true;
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
                "Gate {Gate} resolved at {Icao} to {Identity} (GSX name '{GsxName}', {Confidence}, sources {Sources}{Caveat}); will send {Tokens}",
                requested, icao, match.Parking.Identity.DefaultDisplayName, match.Parking.DisplayName,
                match.Confidence, match.Parking.Sources, match.Caveat is null ? "" : $"; {match.Caveat}",
                string.Join(" → ", match.GsxTokens));
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
                caveat = match.Caveat,
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
    /// whose bglName/uiName names the resolved stand, and returns at once when the candidates
    /// are the stand's suffix variants (the name rungs cannot find a stand the scenery split);
    /// any other outcome returns at once for the caller's revoke/force handling.</summary>
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
                else if (GsxGateResolver.SuffixVariants(candidates, requested) is not null)
                {
                    return result;
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
            case "no_airport":
                // Not a failure (issue #75, 2026-10-10): GSX answered the pick with its own
                // "Select Position at EGCC" page and loaded the airport minutes later; the
                // next airport patch re-dispatches. Armed, airport pending.
                lock (_gate)
                {
                    _lastNoAirportAtUtc = DateTimeOffset.UtcNow;
                }

                SetStatus(GsxGateRequestStatus.Armed, "airport pending — GSX has no airport loaded yet (no_airport); the gate goes out when the destination loads");
                return;

            case "gsx_not_running" or "not_connected":
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

    private void OnPhaseChanged(object? sender, FlightPhaseChangedEventArgs e)
    {
        // The suffix question's deadline (issue #156): at taxi-in the first variant goes out
        // unless the pilot answered — a gate.select is still accepted while rolling.
        if (e.Current == FlightPhase.TaxiIn)
        {
            DefaultPendingVariant();
        }

        _ = TryDispatchAsync();
    }

    private void DefaultPendingVariant()
    {
        string? requested;
        GsxGateRef? first;
        lock (_gate)
        {
            requested = _requestedGate;
            first = _pendingVariants?.FirstOrDefault();
        }

        if (requested is null || first is null)
        {
            return;
        }

        var label = GsxGateResolver.CandidateLabel(first);
        if (_gateChoice is null || !_gateChoice.Choose(first.ResendToken ?? label, "default at taxi-in"))
        {
            // No store (or the question already withdrawn): send the default directly.
            _ = DispatchChosenVariantAsync(new GateChoice(requested, new GateCandidate(label, first.ResendToken ?? label, null), "default at taxi-in", DateTimeOffset.UtcNow));
        }
    }

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
        // pre-2026-09-21 wiring listened to handlerData only. An airport patch is also the
        // end of a no_airport wait, so the backoff does not apply to it.
        if (string.Equals(key, "handlerData", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "airport", StringComparison.OrdinalIgnoreCase))
        {
            lock (_gate)
            {
                if (string.Equals(_api.Mirror.AirportIcao, _destination.Value?.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    _lastNoAirportAtUtc = null;
                }
            }

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
            _lastAirportWalkAtUtc = null;
            _lastNoAirportAtUtc = null;
            _pendingVariants = null;
        }

        _gateChoice?.ClearQuestion();
        if (rearm && Status is GsxGateRequestStatus.Assigned or GsxGateRequestStatus.Confirmed
            or GsxGateRequestStatus.AssignedUnconfirmed or GsxGateRequestStatus.Dispatching)
        {
            SetStatus(GsxGateRequestStatus.Armed, "re-armed after GSX engine restart");
            _ = TryDispatchAsync();
        }
    }

    private void OnReadbackChanged(object? sender, EventArgs e) => CheckReadback();
}
