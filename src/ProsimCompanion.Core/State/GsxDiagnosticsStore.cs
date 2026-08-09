namespace ProsimCompanion.Core.State;

/// <summary><see cref="Stage"/> is the LATCHED lifecycle stage and is what status surfaces
/// must render: the raw mirror has no memory, so GSX flipping a finished quick service (or
/// refuel) back to "available" would otherwise read as never-run (issue #29). The raw
/// semantic/mapped states stay for diagnostics.</summary>
public sealed record GsxServiceView(
    string Id,
    string DisplayName,
    string? SemanticState,
    string MappedState,
    bool CanTrigger,
    bool Waiting,
    string? ProgressText,
    GsxServiceStage Stage = GsxServiceStage.Waiting);

public sealed record GsxCommandView(
    DateTimeOffset Timestamp,
    string Verb,
    string Args,
    bool Ok,
    string Code);

public sealed record GsxDecisionView(DateTimeOffset Timestamp, string Action, string Reason);

/// <summary>GSX boarding/deboarding progress counters for the Flight Status page. Null fields
/// mean "not known yet" (GSX absent or no pax session armed) and render as "—", matching the
/// Prosim2GSX rows Pax Target / Pax Total (B|D) / Cargo (B|D).</summary>
public sealed record GsxBoardingCountersView(
    int? PaxTarget,
    int? PaxBoarded,
    int? PaxDeboarded,
    double? CargoBoardedPercent,
    double? CargoDeboardedPercent);

/// <summary>Most recent GSX service lifecycle edge ("handler event" in Prosim2GSX terms).</summary>
public sealed record GsxHandlerEventView(DateTimeOffset Timestamp, string Service, string Event);

/// <summary>One departure-service row on the status board (the Prosim2GSX-style at-a-glance
/// view): where the service is in its cycle and, when held/skipped, why.</summary>
public sealed record GsxServiceBoardRow(string ServiceId, GsxServiceStage Stage, string? Detail);

/// <summary>Departure-service progression for the status board, in display priority order.</summary>
public enum GsxServiceStage
{
    /// <summary>Not yet eligible (sequence not started, ground prep running, or plan gate).</summary>
    Waiting,

    /// <summary>Eligible but holding — <see cref="GsxServiceBoardRow.Detail"/> carries the reason.</summary>
    Held,

    /// <summary>Not offered / unavailable / bypassed this turnaround.</summary>
    Skipped,

    /// <summary>Trigger sent; GSX has not yet picked it up.</summary>
    Called,

    /// <summary>GSX accepted the request (crew en route).</summary>
    Requested,

    /// <summary>Service running.</summary>
    Active,

    /// <summary>Cycle finished.</summary>
    Completed,
}

public sealed record GsxDiagnosticsSnapshot(
    string Readiness,
    IReadOnlyList<string> Capabilities,
    string? AirportIcao,
    string? GateContextKey,
    string? StartupSid,
    bool MenuShown,
    string? MenuTitle,
    IReadOnlyList<string> MenuEntries,
    IReadOnlyList<GsxServiceView> Services,
    IReadOnlyList<GsxCommandView> RecentCommands)
{
    public static GsxDiagnosticsSnapshot Empty { get; } =
        new("Disconnected", [], null, null, null, false, null, [], [], []);

    /// <summary>Automation phase label (set by the GSX layer alongside Update).</summary>
    public string AutomationPhase { get; init; } = "Inactive";

    /// <summary>Armed/last gate request summary, e.g. "B12: Confirmed — confirmed as B12".</summary>
    public string? GateRequest { get; init; }

    /// <summary>Automation decisions, newest first (filled in by Snapshot()).</summary>
    public IReadOnlyList<GsxDecisionView> RecentDecisions { get; init; } = [];

    /// <summary>Departure-service status board rows in configured order (filled in by
    /// Snapshot(); pushed by the automation layer on every sequencing evaluation).</summary>
    public IReadOnlyList<GsxServiceBoardRow> ServiceBoard { get; init; } = [];

    /// <summary>Boarding/deboarding counters (filled in by Snapshot(); pushed by the boarding
    /// sync at most once per second). Null until GSX reports any pax/cargo activity.</summary>
    public GsxBoardingCountersView? BoardingCounters { get; init; }

    /// <summary>Last service lifecycle edge (filled in by Snapshot()).</summary>
    public GsxHandlerEventView? LastHandlerEvent { get; init; }

    /// <summary>ProSim's ground-power state (the GPU physically attached) — dataref truth, not
    /// the GSX mirror: GSX flips its GPU service back to "available" while the unit stays
    /// connected. Null until the dataref has reported (issue #33).</summary>
    public bool? GroundPowerConnected { get; init; }
}

/// <summary>Arms/cancels arrival-gate requests from UI surfaces (implemented by the GSX layer;
/// status is visible through <see cref="GsxDiagnosticsStore"/>).</summary>
public interface IGsxGateControl
{
    void RequestGate(string gate);

    void Cancel();
}

/// <summary>Drives the departure service sequence from UI surfaces (implemented by the GSX
/// automation layer). <see cref="ForceNext"/> is the INT/RAD "smart button": call the next
/// departure service now, bypassing its activation rule — including Manual entries (the classic
/// "start Boarding while Refueling runs").</summary>
public interface IGsxDepartureControl
{
    /// <summary>True once the departure sequence has been started (manually or automatically).</summary>
    bool Started { get; }

    /// <summary>True once every departure service completed or was skipped.</summary>
    bool Complete { get; }

    /// <summary>Starts the departure service sequence (idempotent).</summary>
    void Start();

    /// <summary>Single-shot: the next sequencing evaluation treats the current step's
    /// activation rule as satisfied. The flight-plan gate still applies.</summary>
    void ForceNext();
}

/// <summary>
/// Live GSX diagnostics for the web UI — the browser-side twin of the wire trace, built for
/// evaluating sim smoke tests at a glance. The GSX layer pushes updates; readers subscribe to
/// <see cref="Changed"/> and read <see cref="Snapshot"/> (no per-reader polling). Kept in Core
/// so the Web project (which references only Core) can render it.
/// </summary>
public sealed class GsxDiagnosticsStore
{
    public const int RecentCommandLimit = 25;
    public const int RecentDecisionLimit = 50;

    private readonly object _gate = new();
    private readonly Queue<GsxCommandView> _commands = new();
    private readonly Queue<GsxDecisionView> _decisions = new();
    private IReadOnlyList<GsxServiceBoardRow> _serviceBoard = [];
    private GsxBoardingCountersView? _boardingCounters;
    private GsxHandlerEventView? _lastHandlerEvent;
    private bool? _groundPowerConnected;
    private GsxDiagnosticsSnapshot _current = GsxDiagnosticsSnapshot.Empty;

    /// <summary>Raised after any update, on the writer's thread — consumers marshal to their
    /// own context (<c>InvokeAsync</c> in Blazor components).</summary>
    public event EventHandler? Changed;

    /// <summary>Point-in-time diagnostics view (recent commands/decisions newest-first).</summary>
    public GsxDiagnosticsSnapshot Snapshot()
    {
        lock (_gate)
        {
            return _current with
            {
                RecentCommands = [.. _commands.Reverse()],
                RecentDecisions = [.. _decisions.Reverse()],
                ServiceBoard = _serviceBoard,
                BoardingCounters = _boardingCounters,
                LastHandlerEvent = _lastHandlerEvent,
                GroundPowerConnected = _groundPowerConnected,
            };
        }
    }

    /// <summary>Replaces the departure-service status board (automation layer, every pump).</summary>
    public void UpdateServiceBoard(IReadOnlyList<GsxServiceBoardRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        lock (_gate)
        {
            _serviceBoard = rows;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Replaces the boarding/deboarding counters (boarding sync, at most 1 Hz —
    /// callers only push on change).</summary>
    public void UpdateBoardingCounters(GsxBoardingCountersView? counters)
    {
        lock (_gate)
        {
            _boardingCounters = counters;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Replaces the ground-power (GPU attached) state — pushed by the ground
    /// equipment sync on dataref change; null = not reported yet.</summary>
    public void UpdateGroundPower(bool? connected)
    {
        lock (_gate)
        {
            _groundPowerConnected = connected;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Records the most recent service lifecycle edge for the Flight Status row.</summary>
    public void RecordHandlerEvent(GsxHandlerEventView handlerEvent)
    {
        ArgumentNullException.ThrowIfNull(handlerEvent);
        lock (_gate)
        {
            _lastHandlerEvent = handlerEvent;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Replaces the connection/mirror-derived portion of the view.</summary>
    public void Update(GsxDiagnosticsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            _current = snapshot;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Appends a command outcome to the bounded recent-commands ring.</summary>
    public void RecordCommand(GsxCommandView command)
    {
        ArgumentNullException.ThrowIfNull(command);
        lock (_gate)
        {
            _commands.Enqueue(command);
            while (_commands.Count > RecentCommandLimit)
            {
                _ = _commands.Dequeue();
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Appends an automation decision ("what happened and why") to the bounded ring —
    /// the primary smoke-test evaluation surface for the automation layer.</summary>
    public void RecordDecision(GsxDecisionView decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        lock (_gate)
        {
            _decisions.Enqueue(decision);
            while (_decisions.Count > RecentDecisionLimit)
            {
                _ = _decisions.Dequeue();
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
