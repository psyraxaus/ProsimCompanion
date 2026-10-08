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
/// the Prosim2GSX <c>ReloadSimbrief</c> port): a keyword intent that opens the gate menu
/// ("Activate Services at …") and picks the line containing "SimBrief" — never an ordinal
/// (the predecessor's v1 was a fixed <c>Select(15)</c>). Runs (a) once per new OFP behind
/// <c>gsx.reloadSimbriefOnNewOfp</c>, armed on the OFP change and fired as soon as GSX is
/// Ready with the aircraft parked at the gate — never in flight, never while taxiing; a
/// plan that arrives airborne simply waits for the next time the aircraft is at a gate with
/// that OFP still current — and (b) on demand from the Status section button, the FO and
/// <c>gsx.reloadSimbrief</c>. Verification: GSX patches <c>handlerData</c> on a SimBrief
/// reload (integration guide §8.13), so that patch — or the menu closing/moving — is the
/// success signal; a pick GSX acknowledged without either is reported "sent, unconfirmed",
/// as the predecessor had no clean signal at all. A menu this service opened is closed
/// again afterwards. Startup module: construction wires the events; the host calls
/// <see cref="Start"/>.
/// </summary>
public sealed class GsxSimbriefReloadService : Core.Hosting.IStartupModule, IGsxSimbriefReloadControl, IDisposable
{
    /// <summary>The gate-menu title anchor (docs/integrations/gsx.md §1).</summary>
    internal const string GateMenuTitle = "Activate Services at";

    /// <summary>The line to pick, as the predecessor inferred it (contains "SimBrief").
    /// Case-insensitive; a hyphenated "Sim-Brief" or "SimBrief reload" both match.</summary>
    internal static readonly Regex SimbriefLine = new(@"sim-?brief", RegexOptions.IgnoreCase | RegexOptions.Compiled);

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
            return Report(source, requestId, new(GsxSimbriefReloadStatus.NotPerformed, "a SimBrief reload is already running"));
        }

        try
        {
            if (!AtGateAndReady(out var why))
            {
                return Report(source, requestId, new(GsxSimbriefReloadStatus.NotPerformed, why));
            }

            var mirror = _api.Mirror;
            var openedByUs = !mirror.MenuShown;
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
                TitlePrefixes = [GateMenuTitle],
                EntryPattern = SimbriefLine,
                // Verify runs only after menu.pick was accepted, so its first call is the
                // "sent" mark. Success = GSX re-read the plan (handlerData patched) or the
                // menu reacted (closed / moved off the gate page).
                Verify = m =>
                {
                    pickSent = true;
                    return handlerDataPatched || !m.MenuShown
                        || m.Menu?.Title.StartsWith(GateMenuTitle, StringComparison.OrdinalIgnoreCase) != true;
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

            GsxSimbriefReloadOutcome outcome;
            if (result.Succeeded)
            {
                outcome = new(GsxSimbriefReloadStatus.Reloaded,
                    handlerDataPatched
                        ? $"GSX re-read the SimBrief plan ({result.Detail}; handler data patched)"
                        : $"picked the SimBrief line ({result.Detail}; menu reacted)");
            }
            else if (pickSent)
            {
                outcome = new(GsxSimbriefReloadStatus.SentUnconfirmed,
                    "the SimBrief line was picked and acknowledged but GSX gave no signal within 10 s — check the VDGS");
            }
            else
            {
                outcome = new(GsxSimbriefReloadStatus.NotPerformed, $"{result.Outcome}: {result.Detail} — menu left for the user");
            }

            // A line that is not on GSX's menu (or matches twice) will not appear on the next
            // phase edge either: retrying the automatic reload would only re-raise the menu
            // at the pilot every few seconds. Transient failures (no response, wrong title)
            // stay pending and are retried on the next readiness / phase / OFP edge.
            var lineMissing = !result.Succeeded && !pickSent
                && result.Outcome is GsxIntentOutcome.ItemNotAvailable or GsxIntentOutcome.AmbiguousMatch;
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

                // The gate menu we raised has done its job; GSX re-shows it after the pick.
                // A missing line leaves the menu for the user (safe-fail rule) — never closed.
                if (outcome.Status != GsxSimbriefReloadStatus.NotPerformed && openedByUs && mirror.MenuShown)
                {
                    var close = await _api.SendCommandAsync("menu.close", null, cancellationToken).ConfigureAwait(false);
                    _logger.LogDebug("SimBrief reload: closed the gate menu we opened ({Code})", close.Code);
                }
            }

            return Report(source, requestId, outcome);
        }
        finally
        {
            _runLock.Release();
        }
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

    private GsxSimbriefReloadOutcome Report(string source, string? requestId, GsxSimbriefReloadOutcome outcome)
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
        });
        return outcome;
    }
}
