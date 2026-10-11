using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Gsx.Menu;
using ProsimCompanion.Gsx.Protocol;

namespace ProsimCompanion.Gsx.Sync;

/// <summary>
/// Asks GSX to re-read the SimBrief flight plan for its VDGS / marshaller boards (2026-10-09,
/// the Prosim2GSX <c>ReloadSimbrief</c> port): a keyword intent that walks the gate menu
/// ("Activate Services at …"), then "Additional Services ▶" and its "Next Page ▶" pages
/// (issue #174, 2026-10-11: the row was on neither the gate menu nor page 1 of Additional
/// Services on any recorded flight, and page 2 had never been opened; the GSX manual calls it
/// the "Reload Simbrief" button and shows the integration only with a SimBrief username set
/// in GSX's own settings — docs/integrations/gsx.md §4) and picks the row matching
/// <see cref="SimbriefLine"/> — never an ordinal (the predecessor's v1 was a fixed
/// <c>Select(15)</c>). Runs (a) once per new OFP behind <c>gsx.reloadSimbriefOnNewOfp</c>,
/// armed on the OFP change and fired as soon as GSX is Ready with the aircraft parked at the
/// gate — never in flight, never while taxiing; a plan that arrives airborne simply waits for
/// the next time the aircraft is at a gate with that OFP still current — and (b) on demand
/// from the Status section button, the FO and <c>gsx.reloadSimbrief</c>. Verification: GSX
/// patches <c>handlerData</c> on a SimBrief reload (integration guide §8.13), so that patch —
/// or the menu closing/moving — is the success signal; a pick GSX acknowledged without either
/// is reported "sent, unconfirmed", as the predecessor had no clean signal at all. A menu this
/// service opened is closed again afterwards, pages walked or not. When no page has the row
/// the Status card says so once per app session and the OFP is not retried. Startup module:
/// construction wires the events; the host calls <see cref="Start"/>.
/// </summary>
public sealed class GsxSimbriefReloadService : Core.Hosting.IStartupModule, IGsxSimbriefReloadControl, IDisposable
{
    /// <summary>The gate-menu title anchor (docs/integrations/gsx.md §1).</summary>
    internal const string GateMenuTitle = "Activate Services at";

    /// <summary>GSX's second-level services page (docs/integrations/gsx.md §1).</summary>
    internal const string AdditionalServicesTitle = "Additional Services";

    /// <summary>The row to pick: "Reload Simbrief" (the manual's name), a hyphenated
    /// "Sim-Brief", "SimBrief reload", "Reload flight plan" … — case-insensitive. Status rows
    /// such as "Flight plan not loaded" match too; GSX greys a dead row and the executor
    /// refuses greyed rows.</summary>
    internal static readonly Regex SimbriefLine = new(@"sim-?brief|flight\s*plan|reload.*plan", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The "Additional Services ▶" row of the gate menu.</summary>
    internal static readonly Regex AdditionalServicesRow = new(@"^\s*additional\s+services\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly TimeSpan VerifyBudget = TimeSpan.FromSeconds(10);

    private readonly IGsxRemoteApi _api;
    private readonly GsxMenuIntentExecutor _executor;
    private readonly OfpStore _ofpStore;
    private readonly IFlightPhaseSource _flightState;
    private readonly IOptionsMonitor<GsxOptions> _options;
    private readonly GsxDiagnosticsStore _diagnostics;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<GsxSimbriefReloadService> _logger;
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private readonly object _gate = new();
    private string? _pendingRequestId;
    private string? _doneRequestId;
    private bool _rowMissingReported;

    public GsxSimbriefReloadService(
        IGsxRemoteApi api,
        GsxMenuIntentExecutor executor,
        OfpStore ofpStore,
        IFlightPhaseSource flightState,
        IOptionsMonitor<GsxOptions> options,
        GsxDiagnosticsStore diagnostics,
        JsonlEventLog eventLog,
        ILogger<GsxSimbriefReloadService> logger)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(ofpStore);
        ArgumentNullException.ThrowIfNull(flightState);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _api = api;
        _executor = executor;
        _ofpStore = ofpStore;
        _flightState = flightState;
        _options = options;
        _diagnostics = diagnostics;
        _eventLog = eventLog;
        _logger = logger;
    }

    public void Start()
    {
        _ofpStore.Changed += OnOfpChanged;
        _api.ReadinessChanged += OnReadinessChanged;
        _flightState.PhaseChanged += OnPhaseChanged;
        _api.Mirror.SidChanged += OnSidChanged;
        // An OFP already in the store at startup (restored from disk) counts as new for a
        // fresh GSX session: GSX has not seen it either.
        OnOfpChanged(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _ofpStore.Changed -= OnOfpChanged;
        _api.ReadinessChanged -= OnReadinessChanged;
        _flightState.PhaseChanged -= OnPhaseChanged;
        _api.Mirror.SidChanged -= OnSidChanged;
        _runLock.Dispose();
    }

    private void OnOfpChanged(object? sender, EventArgs e)
    {
        var ofp = _ofpStore.Current;
        if (ofp is null || string.IsNullOrWhiteSpace(ofp.RequestId))
        {
            return;
        }

        lock (_gate)
        {
            if (string.Equals(ofp.RequestId, _doneRequestId, StringComparison.Ordinal)
                || string.Equals(ofp.RequestId, _pendingRequestId, StringComparison.Ordinal))
            {
                return;
            }

            _pendingRequestId = ofp.RequestId;
        }

        TryAutoReload();
    }

    private void OnReadinessChanged(GsxReadiness readiness) => TryAutoReload();

    private void OnPhaseChanged(object? sender, FlightPhaseChangedEventArgs e) => TryAutoReload();

    /// <summary>A Couatl restart is a new GSX session that has not read the plan: the
    /// current OFP becomes pending again.</summary>
    private void OnSidChanged(string? oldSid, string? newSid)
    {
        lock (_gate)
        {
            _doneRequestId = null;
            _pendingRequestId = _ofpStore.Current?.RequestId;
        }

        TryAutoReload();
    }

    private void TryAutoReload()
    {
        string? requestId;
        lock (_gate)
        {
            requestId = _pendingRequestId;
        }

        if (requestId is null || !_options.CurrentValue.ReloadSimbriefOnNewOfp)
        {
            return;
        }

        if (!AtGateAndReady(out _))
        {
            return; // re-tried on the next readiness / phase / OFP edge
        }

        _ = RunAsync("new OFP", requestId);
    }

    public async Task<GsxSimbriefReloadOutcome> ReloadAsync(string source, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        try
        {
            return await RunAsync(source, _ofpStore.Current?.RequestId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SimBrief reload ({Source}) failed", source);
            return new(GsxSimbriefReloadStatus.NotPerformed, $"The reload failed unexpectedly: {ex.Message}");
        }
    }

    private async Task<GsxSimbriefReloadOutcome> RunAsync(string source, string? requestId, CancellationToken cancellationToken = default)
    {
        if (!await _runLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return Report(source, requestId, new(GsxSimbriefReloadStatus.NotPerformed, "a SimBrief reload is already running"), rowMissing: false, pagesSeen: []);
        }

        try
        {
            if (!AtGateAndReady(out var why))
            {
                return Report(source, requestId, new(GsxSimbriefReloadStatus.NotPerformed, why), rowMissing: false, pagesSeen: []);
            }

            var mirror = _api.Mirror;
            var openedByUs = !mirror.MenuShown;
            var walk = await WalkToSimbriefRowAsync(cancellationToken).ConfigureAwait(false);

            GsxSimbriefReloadOutcome outcome;
            var lineMissing = false;
            if (walk.Result.Succeeded)
            {
                outcome = new(GsxSimbriefReloadStatus.Reloaded,
                    walk.HandlerDataPatched
                        ? $"GSX re-read the SimBrief plan ({walk.Result.Detail} on {walk.PageName}; handler data patched)"
                        : $"picked the SimBrief line ({walk.Result.Detail} on {walk.PageName}; menu reacted)");
            }
            else if (walk.PickSent)
            {
                outcome = new(GsxSimbriefReloadStatus.SentUnconfirmed,
                    $"the SimBrief line was picked on {walk.PageName} and acknowledged but GSX gave no signal within 10 s — check the VDGS");
            }
            else if (walk.RowMissing)
            {
                // The row is on no page this GSX shows: retrying on the next phase edge would
                // only re-raise the menu at the pilot every few seconds.
                lineMissing = true;
                outcome = new(GsxSimbriefReloadStatus.NotPerformed,
                    $"no SimBrief row on any page — this GSX version shows no SimBrief row (pages seen: {string.Join("; ", walk.PagesSeen)})");
            }
            else
            {
                outcome = new(GsxSimbriefReloadStatus.NotPerformed, $"{walk.Result.Outcome}: {walk.Result.Detail} — menu left for the user");
            }

            if (outcome.Status != GsxSimbriefReloadStatus.NotPerformed || lineMissing)
            {
                lock (_gate)
                {
                    _doneRequestId = requestId;
                    if (string.Equals(_pendingRequestId, requestId, StringComparison.Ordinal))
                    {
                        _pendingRequestId = null;
                    }
                }

                // The menu we raised has done its job — or we walked it to a page the pilot
                // never asked for. Either way a menu WE opened goes away again; one the pilot
                // had open stays exactly where the walk left it.
                if (openedByUs && mirror.MenuShown)
                {
                    var close = await _api.SendCommandAsync("menu.close", null, cancellationToken).ConfigureAwait(false);
                    _logger.LogDebug("SimBrief reload: closed the menu we opened ({Code})", close.Code);
                }
            }

            return Report(source, requestId, outcome, lineMissing, walk.PagesSeen);
        }
        finally
        {
            _runLock.Release();
        }
    }

    private sealed record WalkResult(
        GsxIntentResult Result,
        bool PickSent,
        bool HandlerDataPatched,
        bool RowMissing,
        string PageName,
        IReadOnlyList<string> PagesSeen);

    /// <summary>
    /// Gate menu → "Additional Services ▶" → "Next Page ▶" × <c>gsx.simbriefReloadMenuPages</c>,
    /// picking the first row that matches on whichever page shows it. Every hop is an intent
    /// through the executor (safe-fail, TOCTOU re-resolve); a hop that fails ends the walk
    /// with its result. RowMissing is true only when every reachable page was read.
    /// </summary>
    private async Task<WalkResult> WalkToSimbriefRowAsync(CancellationToken cancellationToken)
    {
        var mirror = _api.Mirror;
        var pagesSeen = new List<string>();
        var maxPages = Math.Max(0, _options.CurrentValue.SimbriefReloadMenuPages);

        // Reach the gate menu (navigation-only intent: open + title check, no pick).
        var reach = await _executor.ExecuteAsync(new GsxMenuIntent
        {
            Name = "SimBrief reload (gate menu)",
            TitlePrefixes = [GateMenuTitle],
        }, cancellationToken).ConfigureAwait(false);
        if (!reach.Succeeded)
        {
            return new(reach, false, false, false, GateMenuTitle, pagesSeen);
        }

        var onGateMenu = true;
        for (var hop = 0; hop <= maxPages + 1; hop++)
        {
            var menu = mirror.MenuShown ? mirror.Menu : null;
            if (menu is null)
            {
                return new(new GsxIntentResult(GsxIntentOutcome.GsxNoResponse, "menu vanished during the walk"), false, false, false, "", pagesSeen);
            }

            pagesSeen.Add(GsxMenuPaging.DescribePage(menu, pagesSeen.Count));
            if (menu.Entries.Any(SimbriefLine.IsMatch))
            {
                var pick = await PickSimbriefRowAsync(menu.Title, cancellationToken).ConfigureAwait(false);
                return pick with { PagesSeen = pagesSeen };
            }

            GsxIntentResult? hopResult;
            if (onGateMenu)
            {
                if (!menu.Entries.Any(AdditionalServicesRow.IsMatch))
                {
                    break;
                }

                hopResult = await _executor.ExecuteAsync(new GsxMenuIntent
                {
                    Name = "SimBrief reload (Additional Services)",
                    TitlePrefixes = [GateMenuTitle],
                    EntryPattern = AdditionalServicesRow,
                    Verify = m => m.MenuShown && m.Menu?.Title.StartsWith(AdditionalServicesTitle, StringComparison.OrdinalIgnoreCase) == true,
                }, cancellationToken).ConfigureAwait(false);
                onGateMenu = false;
            }
            else
            {
                if (pagesSeen.Count - 1 >= maxPages)
                {
                    break; // the bound: the gate menu plus maxPages Additional Services pages
                }

                hopResult = await GsxMenuPaging.TurnPageAsync(_executor, "SimBrief reload (next page)", [AdditionalServicesTitle], menu.Entries, cancellationToken).ConfigureAwait(false);
                if (hopResult is null)
                {
                    break; // the last page
                }
            }

            if (!hopResult.Succeeded)
            {
                return new(hopResult, false, false, false, menu.Title, pagesSeen);
            }
        }

        return new(
            new GsxIntentResult(GsxIntentOutcome.ItemNotAvailable, $"no entry matches {SimbriefLine} on {pagesSeen.Count} page(s)"),
            false, false, true, "", pagesSeen);
    }

    private async Task<WalkResult> PickSimbriefRowAsync(string pageTitle, CancellationToken cancellationToken)
    {
        var mirror = _api.Mirror;
        var handlerDataPatched = false;
        var pickSent = false;
        void OnMirrorUpdated(string key)
        {
            if (key.Equals("handlerData", StringComparison.OrdinalIgnoreCase))
            {
                handlerDataPatched = true;
            }
        }

        var intent = new GsxMenuIntent
        {
            Name = "SimBrief reload",
            TitlePrefixes = [pageTitle],
            EntryPattern = SimbriefLine,
            // Verify runs only after menu.pick was accepted, so its first call is the
            // "sent" mark. Success = GSX re-read the plan (handlerData patched) or the
            // menu reacted (closed / moved off this page).
            Verify = m =>
            {
                pickSent = true;
                return handlerDataPatched || !m.MenuShown
                    || m.Menu?.Title.Equals(pageTitle, StringComparison.OrdinalIgnoreCase) != true;
            },
            VerifyTimeout = VerifyBudget,
        };

        mirror.Updated += OnMirrorUpdated;
        GsxIntentResult result;
        try
        {
            result = await _executor.ExecuteAsync(intent, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            mirror.Updated -= OnMirrorUpdated;
        }

        // A row that matched twice on one page is as unusable as a missing one.
        var rowMissing = !result.Succeeded && !pickSent && result.Outcome is GsxIntentOutcome.AmbiguousMatch;
        return new(result, pickSent, handlerDataPatched, rowMissing, $"'{pageTitle}'", []);
    }

    /// <summary>The safe moment: GSX Ready and the aircraft parked at the gate (ground phase
    /// before pushback, engines off, not rolling) with an OFP to reload.</summary>
    private bool AtGateAndReady(out string reason)
    {
        if (_api.Readiness != GsxReadiness.Ready)
        {
            reason = $"GSX is not ready ({_api.Readiness})";
            return false;
        }

        if (_ofpStore.Current is null)
        {
            reason = "no OFP loaded — fetch the SimBrief plan first";
            return false;
        }

        var view = _flightState.Snapshot();
        var data = view.Data;
        if (!_flightState.CurrentPhase.IsAtGate())
        {
            reason = $"the aircraft is not at the gate ({_flightState.CurrentPhase}) — GSX reads the plan at the stand only";
            return false;
        }

        if (data is not null && (!data.OnGround || data.AnyEngineRunning || data.GroundSpeedKt > 1))
        {
            reason = "the aircraft is moving or an engine is running — wait until parked with engines off";
            return false;
        }

        reason = "";
        return true;
    }

    private GsxSimbriefReloadOutcome Report(string source, string? requestId, GsxSimbriefReloadOutcome outcome, bool rowMissing, IReadOnlyList<string> pagesSeen)
    {
        _logger.LogInformation("GSX SimBrief reload ({Source}): {Status} — {Detail}", source, outcome.Status, outcome.Detail);
        _diagnostics.RecordDecision(new GsxDecisionView(DateTimeOffset.UtcNow, "SimBrief reload", $"{source}: {outcome.Status} — {outcome.Detail}"));
        _eventLog.Record("gsx-decision", new { action = "SimBrief reload", reason = $"{source}: {outcome.Status} — {outcome.Detail}" });
        _eventLog.Record("gsx-simbrief-reload", new
        {
            trigger = source,
            outcome = outcome.Status.ToString(),
            detail = outcome.Detail,
            ofpRequestId = requestId,
            pagesSeen,
            rowMissing,
        });

        // The Status card note (issue #174): once per app session for a missing row — the
        // pages it read are the evidence for the next reader; later outcomes replace it.
        if (rowMissing)
        {
            if (!_rowMissingReported)
            {
                _rowMissingReported = true;
                _logger.LogWarning(
                    "This GSX version shows no SimBrief row on the gate menu or the {Count} Additional Services page(s) read — the reload is off for this OFP; set a SimBrief username in GSX's own settings if it has one. Pages: {Pages}",
                    Math.Max(0, pagesSeen.Count - 1), string.Join("; ", pagesSeen));
                _diagnostics.UpdateSimbriefReload(new GsxSimbriefReloadView(
                    DateTimeOffset.UtcNow, outcome.Status.ToString(),
                    "This GSX version shows no SimBrief row (gate menu + Additional Services pages read). The automatic reload is off for this flight plan; GSX reads SimBrief itself when a SimBrief username is set in its own settings.",
                    RowMissing: true));
            }
        }
        else
        {
            _diagnostics.UpdateSimbriefReload(new GsxSimbriefReloadView(DateTimeOffset.UtcNow, outcome.Status.ToString(), outcome.Detail, RowMissing: false));
        }

        return outcome;
    }
}
