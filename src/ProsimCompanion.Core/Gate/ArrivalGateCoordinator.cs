using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Gate;

/// <summary>Point-in-time view of the arrival-gate workflow for the OFP page: the queued
/// gate, whether it has been dispatched, and one status line per target (ATC via
/// SayIntentions, GSX via gate.select).</summary>
public sealed record ArrivalGateView(string? PendingGate, bool Sent, string AtcStatus, string GsxStatus)
{
    public static ArrivalGateView Empty { get; } = new(null, false, "", "");
}

/// <summary>
/// Two-stage arrival-gate coordinator (Prosim2GSX semantics): Confirm queues the gate and
/// reaching cruise auto-fires it once to BOTH targets — GSX (arm-then-dispatch via
/// <see cref="IGsxGateControl"/>) and SayIntentions ATC (assignGate, airport = OFP
/// destination); Send Now fires both immediately. Either target may be absent from the
/// composition or inactive at send time — the other still fires and its status line
/// explains (degrade, not fail). Queue/trigger rules live in the pure
/// <see cref="ArrivalGatePlan"/>; this class adds locking, dispatch and status text.
/// </summary>
public sealed class ArrivalGateCoordinator : IDisposable
{
    private readonly IFlightPhaseSource _phase;
    private readonly OfpStore _ofp;
    private readonly IGsxGateControl? _gsx;
    private readonly ISayIntentionsGateAssign? _atc;
    private readonly ILogger<ArrivalGateCoordinator> _logger;
    private readonly object _lock = new();
    private readonly ArrivalGatePlan _queue = new();
    private string _atcStatus = "";
    private string _gsxStatus = "";

    public ArrivalGateCoordinator(
        IFlightPhaseSource phase,
        OfpStore ofp,
        IGsxGateControl? gsxGate,
        ISayIntentionsGateAssign? sayIntentions,
        ILogger<ArrivalGateCoordinator> logger)
    {
        ArgumentNullException.ThrowIfNull(phase);
        ArgumentNullException.ThrowIfNull(ofp);
        ArgumentNullException.ThrowIfNull(logger);

        _phase = phase;
        _ofp = ofp;
        _gsx = gsxGate;
        _atc = sayIntentions;
        _logger = logger;

        _phase.PhaseChanged += OnPhaseChanged;
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
            return new(_queue.PendingGate, _queue.Fired, _atcStatus, _gsxStatus);
        }
    }

    /// <summary>Queues the gate for auto-send at cruise. When already at cruise the
    /// transition we would fire on has passed, so the queue fires immediately.</summary>
    public void Confirm(string gate)
    {
        string? queued;
        lock (_lock)
        {
            queued = _queue.Queue(gate);
            if (queued is null)
            {
                return;
            }

            _atcStatus = _atc is null
                ? "SayIntentions integration not available — ATC push will be skipped."
                : _atc.IsActive
                    ? "Queued — sends to ATC at cruise (or Send Now)."
                    : "SayIntentions not active — ATC assignment will be skipped.";
            _gsxStatus = _gsx is null
                ? "GSX gate control unavailable — GSX push will be skipped."
                : "Queued — sends to GSX at cruise (or Send Now).";
        }

        _logger.LogInformation("Arrival gate {Gate} confirmed — auto-sends at cruise", queued);
        RaiseChanged();
        TryAutoFire(_phase.CurrentPhase);
    }

    /// <summary>Fires both targets now: an explicit gate wins over the queued one; with
    /// neither, nothing happens. Returns the dispatch task (UI callers may discard it).</summary>
    public Task SendNow(string? gate = null)
    {
        string? send;
        lock (_lock)
        {
            send = _queue.TakeManual(gate);
        }

        if (send is null)
        {
            return Task.CompletedTask;
        }

        _logger.LogInformation("Arrival gate {Gate} — Send Now", send);
        LastDispatch = DispatchAsync(send);
        return LastDispatch;
    }

    /// <summary>Clears the queue (re-queue allowed after) and cancels any armed GSX request.</summary>
    public void Cancel()
    {
        lock (_lock)
        {
            _queue.Clear();
            _atcStatus = "";
            _gsxStatus = "";
        }

        try
        {
            _gsx?.Cancel();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GSX gate cancel failed");
        }

        RaiseChanged();
    }

    public void Dispose() => _phase.PhaseChanged -= OnPhaseChanged;

    private void OnPhaseChanged(object? sender, FlightPhaseChangedEventArgs e) => TryAutoFire(e.Current);

    private void TryAutoFire(FlightPhase phase)
    {
        string? gate;
        lock (_lock)
        {
            gate = _queue.TakeAuto(phase);
        }

        if (gate is null)
        {
            return;
        }

        _logger.LogInformation("Cruise reached — auto-sending arrival gate {Gate}", gate);
        LastDispatch = DispatchAsync(gate);
    }

    private async Task DispatchAsync(string gate)
    {
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
