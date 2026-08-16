using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.EventLog;
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
/// (docs/integrations/gsx-remote-api.md §6): dispatch only when Ready ∧ airport loaded ∧
/// destination known ∧ loaded airport == destination; otherwise stay armed and re-dispatch on
/// state changes. Success is provisional until the SetGate_* LVAR readback matches (60 s window,
/// checked immediately too). A Couatl restart re-arms the last request.
/// </summary>
public sealed class GsxGateSelectionService : Core.State.IGsxGateControl, IDisposable
{
    private static readonly TimeSpan ConfirmationWindow = TimeSpan.FromSeconds(60);

    /// <summary>Minimum quiet time before an identical failed gate.select may be re-dispatched
    /// (issue #75: mirror updates arrive about once a second and re-invoke the dispatcher — a
    /// failed request re-fired 947 ms after its own failure, twice within the same second).</summary>
    private static readonly TimeSpan FailedRetryBackoff = TimeSpan.FromSeconds(10);

    private readonly IGsxRemoteApi _api;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<GsxGateSelectionService> _logger;
    private readonly IDataRefSubscription _destination;
    private readonly IDataRefSubscription _gateName;
    private readonly IDataRefSubscription _gateNumber;
    private readonly IDataRefSubscription _gateSuffix;
    private readonly object _gate = new();
    private string? _requestedGate;
    private bool _retriedOnce;
    private bool _dispatching;
    private string? _lastFailedGate;
    private long _lastFailedAtTicks;
    private Timer? _confirmationTimer;

    public GsxGateSelectionService(
        IGsxRemoteApi api,
        IProsimDataRefs prosim,
        ISimVars simVars,
        JsonlEventLog eventLog,
        ILogger<GsxGateSelectionService> logger)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(prosim);
        ArgumentNullException.ThrowIfNull(simVars);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _api = api;
        _eventLog = eventLog;
        _logger = logger;

        _destination = prosim.Subscribe(ProsimDataRefNames.FmsDestination, DataRefTier.Infrequent);
        _gateName = simVars.Subscribe(GsxLvarNames.SetGateName, "number", DataRefTier.Normal);
        _gateNumber = simVars.Subscribe(GsxLvarNames.SetGateNumber, "number", DataRefTier.Normal);
        _gateSuffix = simVars.Subscribe(GsxLvarNames.SetGateSuffix, "number", DataRefTier.Normal);

        _api.ReadinessChanged += OnStateChanged;
        _api.Mirror.Updated += OnMirrorUpdated;
        _api.Mirror.SidChanged += OnSidChanged;
        _destination.ValueChanged += OnDataChanged;
        _gateName.ValueChanged += OnReadbackChanged;
        _gateNumber.ValueChanged += OnReadbackChanged;
        _gateSuffix.ValueChanged += OnReadbackChanged;
    }

    public GsxGateRequestStatus Status { get; private set; } = GsxGateRequestStatus.Idle;

    public string? RequestedGate => _requestedGate;

    public string StatusDetail { get; private set; } = "";

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
            _retriedOnce = false;
        }
        SetStatus(GsxGateRequestStatus.Armed, $"armed for {gate}");
        _ = TryDispatchAsync();
    }

    public void Cancel()
    {
        lock (_gate)
        {
            _requestedGate = null;
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

            // Preconditions: Ready, airport context, destination known and matching.
            var airport = _api.Mirror.AirportIcao;
            var destination = _destination.GetValue<string?>(null);
            if (_api.Readiness != GsxReadiness.Ready
                || string.IsNullOrWhiteSpace(airport)
                || string.IsNullOrWhiteSpace(destination)
                || !string.Equals(airport, destination.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return; // stay armed; the state-change wiring re-invokes us
            }

            _dispatching = true;
            requested = _requestedGate;
        }

        try
        {
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

    /// <summary>The retry ladder — at most ONE auto-retry total per user request (the
    /// disambiguation counts as that retry).</summary>
    private async Task DispatchLadderAsync(string requested)
    {
        // GSX matches gate.select tokens against its parking display names, which carry
        // prefixes (" Gate D5" at EHAM — issue #36): resolve the user's token to GSX's own
        // name whenever the mirrored parkings already know it. 2026-08-16 amendment (#75):
        // whitespace is the exception — GSX refused its own leading-space name with
        // not_found, so resolved tokens are now trimmed before sending.
        var sendToken = GsxGateResolver.ResolveCanonical(_api.Mirror.Parkings, requested) ?? requested;
        if (!string.Equals(sendToken, requested, StringComparison.Ordinal))
        {
            _logger.LogInformation("Gate {Gate} resolved to GSX parking name {Canonical}", requested, sendToken);
        }

        var result = await SendSelectAsync(sendToken, revokeServices: false, force: false).ConfigureAwait(false);

        if (!result.Ok && !_retriedOnce)
        {
            switch (result.Code)
            {
                case "not_found":
                    // The parkings may not have been mirrored at first dispatch; the failure
                    // itself proves GSX has an airport loaded, so resolve again and retry with
                    // the canonical name when it differs from what was just refused.
                    var canonical = GsxGateResolver.ResolveCanonical(_api.Mirror.Parkings, requested);
                    if (canonical is not null && !string.Equals(canonical, GsxGateResolver.TrimToken(sendToken), StringComparison.Ordinal))
                    {
                        _retriedOnce = true;
                        _logger.LogInformation(
                            "Gate {Gate} not found as sent; retrying with GSX parking name {Canonical}",
                            requested, canonical);
                        result = await SendSelectAsync(canonical, revokeServices: false, force: false).ConfigureAwait(false);
                        break;
                    }

                    // Nearest-name substitution (issue #75, 2026-08-15 flight: requested "313"
                    // failed while GSX's parking list carried "Stand 313"). Accept the nearest
                    // suggestion ONLY when exactly one candidate is an unambiguous match —
                    // equal after trim, or equal once a known facility prefix is stripped.
                    var substitutes = GsxGateResolver
                        .NearestNames(_api.Mirror.Parkings, requested)
                        .Where(name => GsxGateResolver.IsUnambiguousNearestMatch(requested, name))
                        .Select(GsxGateResolver.TrimToken)
                        .Distinct(StringComparer.Ordinal)
                        .ToList();
                    if (substitutes.Count == 1
                        && !string.Equals(substitutes[0], GsxGateResolver.TrimToken(sendToken), StringComparison.Ordinal))
                    {
                        _retriedOnce = true;
                        _logger.LogInformation(
                            "Gate {Gate} not found; substituting the unambiguous nearest parking {Nearest}",
                            requested, substitutes[0]);
                        _eventLog.Record("gsx-gate-substituted", new { gate = requested, nearest = substitutes[0] });
                        result = await SendSelectAsync(substitutes[0], revokeServices: false, force: false).ConfigureAwait(false);
                    }
                    break;
                case "ambiguous":
                    var candidates = ParseCandidates(result.Error);
                    var candidate = GsxGateResolver.PickUniqueCandidate(candidates, requested);
                    if (candidate?.ResendToken is { Length: > 0 } token)
                    {
                        _retriedOnce = true;
                        _logger.LogInformation("Gate {Gate} ambiguous; retrying with candidate {Token}", requested, token);
                        result = await SendSelectAsync(token, revokeServices: false, force: false).ConfigureAwait(false);
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
                    result = await SendSelectAsync(requested, revokeServices: true, force: false).ConfigureAwait(false);
                    break;

                case "assigned_to_other":
                    _retriedOnce = true;
                    _logger.LogInformation("Gate {Gate} occupied; retrying with force", requested);
                    result = await SendSelectAsync(requested, revokeServices: false, force: true).ConfigureAwait(false);
                    break;
            }
        }

        HandleFinalResult(requested, result);
    }

    private void HandleFinalResult(string requested, GsxCommandResult result)
    {
        if (result.Ok || result.Code is "ok" or "prepared" or "already_selected" or "already_parked")
        {
            _eventLog.Record("gsx-gate-assigned", new { gate = requested, code = result.Code });
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
                var nearest = GsxGateResolver.NearestNames(_api.Mirror.Parkings, requested);
                Fail(nearest.Count > 0
                    ? $"gate not found; nearest: {string.Join(", ", nearest)}"
                    : "gate not found");
                return;

            default:
                Fail($"gate.select failed ({result.Code})");
                return;
        }
    }

    private Task<GsxCommandResult> SendSelectAsync(string gate, bool revokeServices, bool force)
        // Trimmed at the transport (issue #75): a leading space in a mirrored parking name
        // (" Gate D57", 2026-08-15 flight) makes GSX answer not_found for its own gate.
        => _api.SendCommandAsync("gate.select", new JsonObject
        {
            ["gate"] = GsxGateResolver.TrimToken(gate),
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
            _gateName.GetValue(0),
            _gateNumber.GetValue(0),
            _gateSuffix.GetValue(-1));
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

    private void OnMirrorUpdated(string key)
    {
        if (string.Equals(key, "handlerData", StringComparison.OrdinalIgnoreCase))
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
