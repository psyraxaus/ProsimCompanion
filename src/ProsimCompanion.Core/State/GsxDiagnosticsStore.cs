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

/// <summary>Ground-preparation progress for the Flight Status page (issue #45): the
/// coordinator's current stage name (Reposition/Settling/AnchorGate/GroundEquipment/
/// JetwayStairs/Complete) plus a short human reason ("waiting for the position to settle",
/// "holding: MSFS session not active"). Null until the coordinator has reported anything —
/// the page renders "—", exactly like the other not-yet-known rows.</summary>
public sealed record GsxGroundPrepView(string Stage, string Detail);

/// <summary>One departure-service row on the status board (the Prosim2GSX-style at-a-glance
/// view): where the service is in its cycle and, when held/skipped, why.</summary>
public sealed record GsxServiceBoardRow(string ServiceId, GsxServiceStage Stage, string? Detail);

/// <summary>Outcome kinds of the session-start aircraft state check (issue #63).</summary>
public enum AircraftStateCheckStatus
{
    /// <summary>Every evaluable expectation held — the aircraft matches the definition.</summary>
    Pass,

    /// <summary>At least one expectation failed; see the mismatch list.</summary>
    Mismatch,

    /// <summary>The check did not run — <see cref="AircraftStateCheckView.Reason"/> says why
    /// (disabled, airborne restart, turnaround, no definition, datarefs unavailable).</summary>
    Skipped,
}

/// <summary>One failed expectation: the definition row's label plus the spoken phrase the FO
/// advisory uses for it.</summary>
public sealed record AircraftStateMismatchView(string Label, string Phrase);

/// <summary>Verdict of the once-per-session cold-and-dark check (issue #63). Items whose
/// datarefs never reported are listed in <see cref="UncheckedLabels"/> rather than counted as
/// mismatches (fail-open per item — a half-registered ProSim must not condemn the aircraft).
/// <see cref="Announce"/> is decided by the assessor (mismatches on a FRESH departure with the
/// announce option on) so the speech side needs no policy of its own.</summary>
public sealed record AircraftStateCheckView(
    DateTimeOffset Timestamp,
    AircraftStateCheckStatus Status,
    IReadOnlyList<AircraftStateMismatchView> Mismatches,
    IReadOnlyList<string> UncheckedLabels,
    string? Reason,
    bool Announce);

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

    /// <summary>Ground-preparation stage/status (filled in by Snapshot(); pushed by the prep
    /// coordinator on stage transitions and hold changes — issue #45).</summary>
    public GsxGroundPrepView? GroundPrep { get; init; }

    /// <summary>ProSim's ground-power state (the GPU physically attached) — dataref truth, not
    /// the GSX mirror: GSX flips its GPU service back to "available" while the unit stays
    /// connected. Null until the dataref has reported (issue #33).</summary>
    public bool? GroundPowerConnected { get; init; }

    /// <summary>Session-start cold-and-dark verdict (filled in by Snapshot(); pushed once per
    /// session by the aircraft state check, issue #63). Null until the check has run.</summary>
    public AircraftStateCheckView? AircraftStateCheck { get; init; }

    /// <summary>GSX parking conflict (issue #44): GSX is showing its parking-change menu while
    /// the session gate is unknown — it does not recognize the aircraft's position, and no
    /// app restart can fix that. Null when no conflict stands.</summary>
    public GsxParkingConflictView? ParkingConflict { get; init; }
}

/// <summary>One observed parking conflict (issue #44). <see cref="GsxFacility"/> is the
/// facility GSX itself names in its "Change Facility [...]" menu entry — the strongest
/// available hint of where GSX thinks the aircraft is. The FO advisory speaks each
/// <see cref="Timestamp"/> once; the web Flight Status page renders it as long as it stands.</summary>
public sealed record GsxParkingConflictView(DateTimeOffset Timestamp, string GsxFacility);

/// <summary>Arms/cancels arrival-gate requests from UI surfaces (implemented by the GSX layer;
/// status is visible through <see cref="GsxDiagnosticsStore"/>).</summary>
public interface IGsxGateControl
{
    void RequestGate(string gate);

    void Cancel();
}

/// <summary>Re-runs the session-start cold-and-dark check on demand (implemented by the GSX
/// sync layer; verdict lands in <see cref="GsxDiagnosticsStore"/>). The check is once-per-
/// session by design, so without this seam a pilot who fixed the switches — or hit the #59
/// false-airborne latch — could only get a fresh verdict by restarting the app (issue #92).</summary>
public interface IAircraftStateCheckControl
{
    /// <summary>Clears the once-per-session latch and re-assesses immediately. The fresh
    /// verdict is always announced (the pilot explicitly asked, so even a Pass or a Skipped
    /// gets a spoken answer). Returns false when the assessment could not run yet — no sim
    /// session, or the settle guards still holding — so the caller can answer honestly
    /// instead of leaving the request hanging.</summary>
    bool RequestRecheck(string source);
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
/// evaluating sim smoke tests at a glance. The GSX layer pushes updates; readers observe and
/// read <see cref="SnapshotStore{T}.Snapshot"/> (no per-reader polling). Kept in Core so the
/// Web project (which references only Core) can render it. Recomposed on the shared snapshot
/// store (campaign #86): the snapshot record is the single source — a new row is one record
/// property plus one mutator, and unchanged pushes (value-equal views) no longer notify.
/// </summary>
public sealed class GsxDiagnosticsStore : SnapshotStore<GsxDiagnosticsSnapshot>
{
    public const int RecentCommandLimit = 25;
    public const int RecentDecisionLimit = 50;

    private readonly Collections.BoundedLog<GsxCommandView> _commands = new(RecentCommandLimit);
    private readonly Collections.BoundedLog<GsxDecisionView> _decisions = new(RecentDecisionLimit);

    public GsxDiagnosticsStore()
        : base(GsxDiagnosticsSnapshot.Empty)
    {
    }

    /// <summary>Replaces the departure-service status board (automation layer, every pump).</summary>
    public void UpdateServiceBoard(IReadOnlyList<GsxServiceBoardRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        Update(snapshot => snapshot with { ServiceBoard = rows });
    }

    /// <summary>Replaces the boarding/deboarding counters (boarding sync, at most 1 Hz —
    /// callers only push on change).</summary>
    public void UpdateBoardingCounters(GsxBoardingCountersView? counters)
        => Update(snapshot => snapshot with { BoardingCounters = counters });

    /// <summary>Replaces the ground-power (GPU attached) state — pushed by the ground
    /// equipment sync on dataref change; null = not reported yet.</summary>
    public void UpdateGroundPower(bool? connected)
        => Update(snapshot => snapshot with { GroundPowerConnected = connected });

    /// <summary>Replaces the ground-preparation stage/status (prep coordinator, on stage
    /// transitions only — issue #45); null = not reported yet.</summary>
    public void UpdateGroundPrep(GsxGroundPrepView? groundPrep)
        => Update(snapshot => snapshot with { GroundPrep = groundPrep });

    /// <summary>Replaces the session-start aircraft state verdict (aircraft state check, once
    /// per session — issue #63); null = re-armed for a new session, renders "—".</summary>
    public void UpdateAircraftStateCheck(AircraftStateCheckView? check)
        => Update(snapshot => snapshot with { AircraftStateCheck = check });

    /// <summary>Replaces the parking-conflict view (issue #44); null = conflict cleared (the
    /// session gate became known, or the session ended).</summary>
    public void UpdateParkingConflict(GsxParkingConflictView? conflict)
        => Update(snapshot => snapshot with { ParkingConflict = conflict });

    /// <summary>Records the most recent service lifecycle edge for the Flight Status row.</summary>
    public void RecordHandlerEvent(GsxHandlerEventView handlerEvent)
    {
        ArgumentNullException.ThrowIfNull(handlerEvent);
        Update(snapshot => snapshot with { LastHandlerEvent = handlerEvent });
    }

    /// <summary>Replaces the connection/mirror-derived portion of the view; the feature-pushed
    /// rows (board, counters, prep, rings, …) carry over from the current snapshot.</summary>
    public void Update(GsxDiagnosticsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Update(current => snapshot with
        {
            RecentCommands = current.RecentCommands,
            RecentDecisions = current.RecentDecisions,
            ServiceBoard = current.ServiceBoard,
            BoardingCounters = current.BoardingCounters,
            LastHandlerEvent = current.LastHandlerEvent,
            GroundPrep = current.GroundPrep,
            GroundPowerConnected = current.GroundPowerConnected,
            AircraftStateCheck = current.AircraftStateCheck,
            ParkingConflict = current.ParkingConflict,
        });
    }

    /// <summary>Appends a command outcome to the bounded recent-commands ring.</summary>
    public void RecordCommand(GsxCommandView command)
    {
        ArgumentNullException.ThrowIfNull(command);
        _commands.Add(command);
        Update(snapshot => snapshot with { RecentCommands = _commands.Snapshot() });
    }

    /// <summary>Appends an automation decision ("what happened and why") to the bounded ring —
    /// the primary smoke-test evaluation surface for the automation layer.</summary>
    public void RecordDecision(GsxDecisionView decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        _decisions.Add(decision);
        Update(snapshot => snapshot with { RecentDecisions = _decisions.Snapshot() });
    }
}
