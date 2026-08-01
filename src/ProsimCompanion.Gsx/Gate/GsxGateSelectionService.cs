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
        var result = await SendSelectAsync(requested, revokeServices: false, force: false).ConfigureAwait(false);

        if (!result.Ok && !_retriedOnce)
        {
            switch (result.Code)
            {
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
