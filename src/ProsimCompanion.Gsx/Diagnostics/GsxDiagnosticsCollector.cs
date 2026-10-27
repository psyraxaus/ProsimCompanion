using System.Globalization;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Diagnostics.Ecam;
using ProsimCompanion.Core.State;
using ProsimCompanion.Gsx.Automation;
using ProsimCompanion.Gsx.Sync;

namespace ProsimCompanion.Gsx.Diagnostics;

/// <summary>
/// The GSX page: the eight service boxes around the hull, the door markers, the three
/// progress bars and the GSX-specific memo cells. Each box shows GSX's live state for the
/// service first, the departure board's view (held reasons, latched completion) while GSX is
/// idle, and the GPU box shows ProSim's external-power truth; the only judgement made here is
/// the ECAM word for each stage and which rows are "queued" (amber).
/// </summary>
public sealed class GsxDiagnosticsCollector : IDiagnosticsCollector
{
    private const string System = "GSX";

    /// <summary>Node id → the board's service id, in the page's drawing order.</summary>
    public static readonly IReadOnlyList<(string Node, string ServiceId)> Services =
    [
        ("jetway", GsxServiceIds.OperateJetways),
        ("gpu", GsxServiceIds.Gpu),
        ("stairs", GsxServiceIds.OperateStairs),
        ("pushback", GsxServiceIds.Departure),
        ("catering", GsxServiceIds.Catering),
        ("refuel", GsxServiceIds.Refueling),
        ("boarding", GsxServiceIds.Boarding),
        ("deice", GsxServiceIds.DeIce),
    ];

    private readonly GsxDiagnosticsStore _store;
    private readonly IGsxRemoteApi _api;
    private readonly DoorStateReader _doors;
    private readonly DepartureCycleState _cycle;
    private readonly GsxRefuelSync? _refuel;
    private readonly IGsxAutomationPhaseView? _automation;
    private readonly Microsoft.Extensions.Options.IOptionsMonitor<Core.Configuration.GsxOptions>? _options;
    private readonly StateTransitions<string> _edges = new();
    private DateTimeOffset? _turnaroundStartedUtc;

    public GsxDiagnosticsCollector(
        GsxDiagnosticsStore store,
        IGsxRemoteApi api,
        DoorStateReader doors,
        DepartureCycleState cycle,
        GsxRefuelSync? refuel = null,
        IGsxAutomationPhaseView? automation = null,
        Microsoft.Extensions.Options.IOptionsMonitor<Core.Configuration.GsxOptions>? options = null)
    {
        _options = options;
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(doors);
        ArgumentNullException.ThrowIfNull(cycle);
        _store = store;
        _api = api;
        _doors = doors;
        _cycle = cycle;
        _refuel = refuel;
        _automation = automation;
    }

    public string Page => DiagPageIds.Gsx;

    public void Contribute(DiagPageBuilder page, DiagCautionSink cautions)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(cautions);

        var now = page.Events.Now;
        var snap = _store.Snapshot();
        var ready = _api.Readiness == GsxReadiness.Ready;
        var board = snap.ServiceBoard.ToDictionary(r => r.ServiceId, r => r, StringComparer.OrdinalIgnoreCase);
        var live = snap.Services.ToDictionary(s => s.Id, s => s, StringComparer.OrdinalIgnoreCase);

        string? queued = null;
        string? next = null;
        var states = new List<KeyValuePair<string, DiagState>>();
        foreach (var (node, serviceId) in Services)
        {
            if (!ready)
            {
                page.NoData(node);
                continue;
            }

            live.TryGetValue(serviceId, out var view);
            board.TryGetValue(serviceId, out var row);

            // The GPU box is ProSim's external-power truth (the unit physically on the
            // aircraft), not GSX's service state and not the departure board — GSX flips its
            // GPU service back to "available" while the unit stays connected, and the board
            // only knows the services the sequence intends to call (owner report 2026-10-27:
            // "NOT REQ" with the GPU connected). The GSX word rides along as the detail.
            if (node == "gpu" && snap.GroundPowerConnected is { } gpuOn)
            {
                var gsxWord = view is null ? null : "GSX " + StageWord(view.Stage, node, view.Waiting).Value;
                var gpuState = gpuOn ? DiagState.Normal : DiagState.NotApplicable;
                page.Node(node, gpuState, gpuOn ? EcamTokens.Conn : EcamTokens.Off, gpuOn ? "EXT PWR" : row?.Detail?.ToUpperInvariant() ?? gsxWord);
                states.Add(new(node.ToUpperInvariant(), gpuState));
                continue;
            }

            if (view is null && row is null)
            {
                page.NotApplicable(node, EcamTokens.NotReq);
                continue;
            }

            // What GSX is doing right now comes first (a jetway docked by hand is CONN whether
            // or not the sequence asked for it). While GSX sits idle the departure board adds
            // the automation's view — held with its reason, latched completed (#29), skipped.
            var stage = view?.Stage ?? row!.Stage;
            if (row is not null && stage == GsxServiceStage.Waiting)
            {
                stage = row.Stage;
            }

            var detail = row?.Detail ?? view?.ProgressText;
            if (detail is null && view?.Stage == GsxServiceStage.Skipped)
            {
                detail = EcamTokens.Unavail;
            }

            var (state, value) = StageWord(stage, node, view?.Waiting ?? false);
            page.Node(node, state, value, detail?.ToUpperInvariant());
            states.Add(new(node.ToUpperInvariant(), state));
            if (state == DiagState.Caution && queued is null)
            {
                queued = node.ToUpperInvariant();
            }

            // "Next" is the automation's queue, so only a board row can nominate it.
            if (next is null && row is not null && stage is GsxServiceStage.Waiting or GsxServiceStage.Held)
            {
                next = node.ToUpperInvariant();
            }
        }

        // Which service door the catering truck serves (the page places the CATERING box
        // beside it): the user's pick, else the door GSX last asked for, else forward.
        var cateringDoor = _options is null
            ? Core.Configuration.GsxOptions.CateringDoorForward
            : Core.Configuration.GsxOptions.EffectiveCateringDoor(_options.CurrentValue);
        page.Readout("catering_door", cateringDoor == Core.Configuration.GsxOptions.CateringDoorAft ? "4R" : "1R");

        // Door markers on the hull: one readout per open door, "OPEN".
        foreach (var door in _doors.Read())
        {
            page.Readout("door_" + door.Label.Replace(' ', '_').ToLowerInvariant(), door.Known ? (door.Open ? "OPEN" : "CLSD") : null);
        }

        // Progress bars: pax and cargo from the board counters, fuel from the refuel sync.
        var counters = snap.BoardingCounters;
        if (counters?.PaxTarget is > 0)
        {
            page.Gauge("pax", counters.PaxBoarded, 0, counters.PaxTarget.Value, counters.PaxTarget.Value + 1, counters.PaxTarget.Value + 1, "PAX");
        }

        if (counters?.CargoBoardedPercent is { } cargoPct)
        {
            page.Gauge("cargo", cargoPct, 0, 100, 101, 101, EcamTokens.Units.Percent);
        }

        if (_refuel?.Transfer is { } fuel && fuel.TargetKg > 0)
        {
            page.Gauge("fuel", Math.Round(fuel.CurrentKg / 1000, 1), 0, Math.Round(fuel.TargetKg / 1000, 1), fuel.TargetKg, fuel.TargetKg, EcamTokens.Units.Tonnes);
        }

        // Caution line and STATUS entries.
        if (!ready)
        {
            var state = _api.Readiness == GsxReadiness.ConnectedGsxNotRunning ? "COUATL NOT RUNNING" : "GSX NOT CONNECTED";
            page.CautionLine(state);
        }
        else if (queued is not null)
        {
            var line = $"{queued} QUEUED";
            if (next is not null && next != queued)
            {
                line += $" · NEXT SVC {next}";
            }

            page.CautionLine(line);
            var detail = board.Values.FirstOrDefault(r => r.ServiceId.Equals(ServiceIdOf(queued), StringComparison.OrdinalIgnoreCase))?.Detail;
            cautions.Raise(System, $"{queued} QUEUED", DiagState.Caution, new DiagAction("WAIT", detail?.ToUpperInvariant() ?? EcamTokens.NoData));
        }
        else if (next is not null)
        {
            page.CautionLine($"NEXT SVC {next}");
        }

        if (snap.DroppedCall is { } dropped && now - dropped.Timestamp < TimeSpan.FromMinutes(5))
        {
            cautions.Raise(System, $"{dropped.ServiceId.ToUpperInvariant()} CALL DROPPED", DiagState.Caution,
                new DiagAction("MENU", dropped.OpenMenu?.ToUpperInvariant() ?? "BUSY"));
        }

        // Memo cells for this page.
        page.Readout("memo_gsx", _api.Readiness switch
        {
            GsxReadiness.Ready => EcamTokens.Conn,
            GsxReadiness.ConnectedGsxNotRunning => "NO COUATL",
            _ => EcamTokens.Reconn,
        });
        page.Readout("memo_gsx_detail", snap.AirportIcao?.ToUpperInvariant());
        page.Readout("memo_smartbtn", _cycle.Complete ? "DONE" : next ?? (_cycle.Started ? "RUNNING" : "START"));
        page.Readout("memo_automation", (_automation?.Phase.ToString() ?? snap.AutomationPhase).ToUpperInvariant());

        if (_cycle.Started && _turnaroundStartedUtc is null)
        {
            _turnaroundStartedUtc = now;
        }
        else if (!_cycle.Started)
        {
            _turnaroundStartedUtc = null;
        }

        if (_turnaroundStartedUtc is { } started)
        {
            var elapsed = now - started;
            page.Readout("memo_turnaround", $"{(int)elapsed.TotalHours:00} H {elapsed.Minutes:00}");
        }

        foreach (var (id, _, to) in _edges.Observe(states))
        {
            page.Events.Push($"GSX {id} {(to == DiagState.Normal ? "OK" : "QUEUED")}", to);
        }
    }

    /// <summary>The ECAM word for a board stage. Jetway, GPU and stairs say CONN rather than
    /// PROG while active, as the mockup does; a service the automation decided to skip is
    /// white "- - -" with NOT REQ beneath.</summary>
    public static (DiagState State, string Value) StageWord(GsxServiceStage stage, string node, bool waiting)
    {
        var connects = node is "jetway" or "gpu" or "stairs";
        return stage switch
        {
            GsxServiceStage.Skipped => (DiagState.NotApplicable, EcamTokens.NotApplicable),
            GsxServiceStage.Waiting => (DiagState.Normal, EcamTokens.Avail),
            GsxServiceStage.Held => (DiagState.Caution, "HELD"),
            GsxServiceStage.Called => (DiagState.Caution, EcamTokens.Req),
            GsxServiceStage.Requested => (waiting ? DiagState.Caution : DiagState.Normal, EcamTokens.Req),
            GsxServiceStage.Active => (DiagState.Normal, connects ? EcamTokens.Conn : EcamTokens.Prog),
            GsxServiceStage.Completed => (DiagState.Normal, connects ? EcamTokens.Conn : EcamTokens.Done),
            _ => (DiagState.NoData, EcamTokens.NoData),
        };
    }

    private static string ServiceIdOf(string nodeUpper)
        => Services.FirstOrDefault(s => s.Node.Equals(nodeUpper, StringComparison.OrdinalIgnoreCase)).ServiceId ?? nodeUpper;
}

/// <summary>The SIM page's GSX source box (a collector contributes to one page, so the GSX
/// pillar has two): COUATL state and the Remote API readiness.</summary>
public sealed class GsxSourceDiagnosticsCollector : IDiagnosticsCollector
{
    private readonly IGsxRemoteApi _api;
    private readonly ConnectionStatusStore _connections;
    private readonly StateTransitions<string> _edges = new();

    public GsxSourceDiagnosticsCollector(IGsxRemoteApi api, ConnectionStatusStore connections)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(connections);
        _api = api;
        _connections = connections;
    }

    public string Page => DiagPageIds.Sim;

    public void Contribute(DiagPageBuilder page, DiagCautionSink cautions)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(cautions);

        var dot = _connections.Snapshot().FirstOrDefault(p => p.Key == Subsystems.Gsx).Value;
        var (state, value, detail) = dot == ConnectionState.Disabled
            ? (DiagState.NotApplicable, EcamTokens.NotApplicable, "GSX OFF")
            : _api.Readiness switch
            {
                GsxReadiness.Ready => (DiagState.Normal, EcamTokens.Conn, "COUATL"),
                GsxReadiness.ConnectedGsxNotRunning => (DiagState.Caution, EcamTokens.Reconn, "NO COUATL"),
                _ => (DiagState.Caution, EcamTokens.Reconn, "NO API"),
            };
        page.Node("gsx", state, value, detail);
        page.Readout("rate_gsx", _api.Readiness == GsxReadiness.Ready ? "2" : null);
        page.Link("gsx", "simvar_bus");

        if (state == DiagState.Caution)
        {
            cautions.Raise("GSX", _api.Readiness == GsxReadiness.ConnectedGsxNotRunning ? "COUATL NOT RUNNING" : "GSX API LOST",
                DiagState.Caution, new DiagAction("RECONNECT", EcamTokens.Auto));
            cautions.Inop("GROUND SERVICES");
        }

        foreach (var (id, _, to) in _edges.Observe([new("GSX", state)]))
        {
            page.Events.Push($"{id} {(to == DiagState.Normal ? "CONN" : to == DiagState.NotApplicable ? "OFF" : "LOST")}", to);
        }
    }
}
