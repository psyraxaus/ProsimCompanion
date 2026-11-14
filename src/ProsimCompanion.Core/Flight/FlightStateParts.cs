using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Flight;

/// <summary>
/// The OFP as the app holds it (issue #167): the parsed SimBrief plan with the pax/cargo
/// figures the import actually wrote. Restored straight into the <see cref="OfpStore"/> —
/// no EFB write, no <c>ImportAsync</c>, no re-randomised passengers (the 2026-10-10 cruise
/// re-imports rolled 122 → 125 → 126).
/// </summary>
public sealed class OfpSnapshotPart : IFlightStatePart, IDisposable
{
    private readonly OfpStore _store;
    private readonly ILogger<OfpSnapshotPart> _logger;

    public OfpSnapshotPart(OfpStore store, ILogger<OfpSnapshotPart> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _logger = logger;
        _store.Changed += OnChanged;
    }

    public string Key => "ofp";

    /// <summary>First: the fuel log and the identity checks read the OFP.</summary>
    public int RestoreOrder => 10;

    public event Action? Changed;

    public JsonNode? Capture()
        => _store.Current is { } ofp ? JsonSerializer.SerializeToNode(ofp, FlightStateSnapshotStore.JsonOptions) : null;

    public void Restore(JsonNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var ofp = node.Deserialize<OfpData>(FlightStateSnapshotStore.JsonOptions);
        if (ofp is null || ofp.OriginIcao.Length == 0 || ofp.DestinationIcao.Length == 0)
        {
            return;
        }

        _store.Set(ofp);
        _logger.LogInformation(
            "OFP restored from the flight-state snapshot: {Origin}-{Destination} {FlightNumber} (request {RequestId}, {Pax} pax) — EFB untouched",
            ofp.OriginIcao, ofp.DestinationIcao, ofp.FlightNumber, ofp.RequestId, ofp.PaxCount);
    }

    private void OnChanged(object? sender, EventArgs e) => Changed?.Invoke();

    public void Dispose() => _store.Changed -= OnChanged;
}

/// <summary>The crew-entered arrival minima (issue #167): captured at 22:22Z on 2026-10-10,
/// lost at the 22:37Z restart, "minima" callouts degraded, re-captured by ASR as 49 ft.</summary>
public sealed class ArrivalMinimaSnapshotPart : IFlightStatePart, IDisposable
{
    private readonly ArrivalMinimaStore _store;
    private readonly ILogger<ArrivalMinimaSnapshotPart> _logger;
    private readonly IDisposable _subscription;

    public ArrivalMinimaSnapshotPart(ArrivalMinimaStore store, ILogger<ArrivalMinimaSnapshotPart> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _logger = logger;
        _subscription = _store.Observe(_ => Changed?.Invoke());
    }

    public string Key => "minima";

    public int RestoreOrder => 40;

    public event Action? Changed;

    public JsonNode? Capture()
        => _store.Current is { } minima ? JsonSerializer.SerializeToNode(minima, FlightStateSnapshotStore.JsonOptions) : null;

    public void Restore(JsonNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var minima = node.Deserialize<ArrivalMinima>(FlightStateSnapshotStore.JsonOptions);
        if (minima is null || minima.AltitudeFt <= 0)
        {
            return;
        }

        _store.Set(minima);
        _logger.LogInformation("Arrival minima restored from the flight-state snapshot: {Kind} {AltitudeFt:F0} ft", minima.Kind, minima.AltitudeFt);
    }

    public void Dispose() => _subscription.Dispose();
}

/// <summary>What the departure-cycle part persists.</summary>
/// <param name="OffBlocksStamped">The leg had left the gate when this was written: the
/// started/complete flags then describe a departure that is over and are safe to carry;
/// at the gate they are left to the GSX startup resync (issue #30), which reads the
/// tracking LVARs — a reloaded sim flight on the same OFP must not inherit "all services
/// complete".</param>
public sealed record DepartureCycleSnapshot(bool Started, bool Complete, bool IsTurnaround, bool OffBlocksStamped);

/// <summary>The departure cycle flags (issue #167).</summary>
public sealed class DepartureCycleSnapshotPart : IFlightStatePart, IDisposable
{
    private readonly DepartureCycleState _cycle;
    private readonly FlightTimesStore _times;
    private readonly ILogger<DepartureCycleSnapshotPart> _logger;

    public DepartureCycleSnapshotPart(DepartureCycleState cycle, FlightTimesStore times, ILogger<DepartureCycleSnapshotPart> logger)
    {
        ArgumentNullException.ThrowIfNull(cycle);
        ArgumentNullException.ThrowIfNull(times);
        ArgumentNullException.ThrowIfNull(logger);
        _cycle = cycle;
        _times = times;
        _logger = logger;
        _cycle.Changed += OnChanged;
    }

    public string Key => "departureCycle";

    public int RestoreOrder => 50;

    public event Action? Changed;

    public JsonNode? Capture()
    {
        var state = new DepartureCycleSnapshot(_cycle.Started, _cycle.Complete, _cycle.IsTurnaround, _times.Snapshot().OffBlocksUtc is not null);
        return state is { Started: false, Complete: false, IsTurnaround: false }
            ? null
            : JsonSerializer.SerializeToNode(state, FlightStateSnapshotStore.JsonOptions);
    }

    public void Restore(JsonNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var state = node.Deserialize<DepartureCycleSnapshot>(FlightStateSnapshotStore.JsonOptions);
        if (state is null)
        {
            return;
        }

        var restored = new List<string>();
        if (state.IsTurnaround)
        {
            _cycle.MarkTurnaround();
            restored.Add("turnaround");
        }

        if (state.OffBlocksStamped)
        {
            if (state.Started)
            {
                _cycle.MarkStarted();
                restored.Add("started");
            }

            if (state.Complete)
            {
                _cycle.MarkComplete();
                restored.Add("complete");
            }
        }

        if (restored.Count > 0)
        {
            _logger.LogInformation("Departure cycle restored from the flight-state snapshot: {Flags}", string.Join(", ", restored));
        }
    }

    private void OnChanged() => Changed?.Invoke();

    public void Dispose() => _cycle.Changed -= OnChanged;
}

/// <summary>
/// The captain's de-icing answer of the current departure (issue #166, EGLL 2026-11-14):
/// "no de-icing" at 04:20:45Z, an app restart at the stand at 04:32:42Z on the same plan, and
/// GSX's own offer answered "Yes" at 04:40:51Z because the store was back at Pending. The
/// answer now rides the snapshot (<see cref="DeiceVerdictMemory"/>) and is put back into the
/// <see cref="DeiceRequestStore"/>, where the offer handler, the departure sequencer and the
/// Status board read it as before.
/// <para>It ends with the departure it was given for: the flight-cycle reset (arrival, a
/// different plan at the stand) empties the store and the next write leaves the part out;
/// the end of duty empties the store here; and the plan id the answer was given for travels
/// with it, so an answer is neither written nor restored under another plan.</para>
/// </summary>
public sealed class DeiceVerdictSnapshotPart : IFlightStatePart, IDisposable
{
    public const string RestoredEvent = "gsx-deice-verdict-restored";

    private readonly DeiceRequestStore _store;
    private readonly OfpStore _ofp;
    private readonly EventLog.JsonlEventLog _eventLog;
    private readonly ILogger<DeiceVerdictSnapshotPart> _logger;
    private readonly Day.DutyDaySignals? _dutyDay;
    private readonly IDisposable _subscription;
    private readonly object _gate = new();
    private CaptainsAnswer? _answer;
    private string? _answerPlanId;

    public DeiceVerdictSnapshotPart(
        DeiceRequestStore store,
        OfpStore ofp,
        EventLog.JsonlEventLog eventLog,
        ILogger<DeiceVerdictSnapshotPart> logger,
        Day.DutyDaySignals? dutyDay = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(ofp);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _ofp = ofp;
        _eventLog = eventLog;
        _logger = logger;
        _dutyDay = dutyDay;
        _subscription = _store.Observe(OnStoreChanged);
        if (_dutyDay is not null)
        {
            _dutyDay.DayEnded += OnDutyDayEnded;
        }
    }

    public string Key => "deiceVerdict";

    /// <summary>After the OFP (10: the plan the answer is compared with) and the departure
    /// cycle (50), before the Speech pillar's latches.</summary>
    public int RestoreOrder => 55;

    public event Action? Changed;

    public JsonNode? Capture()
    {
        var snapshot = _store.Snapshot();
        string? planId;
        lock (_gate)
        {
            planId = ReferenceEquals(snapshot.Asking.Answer, _answer) ? _answerPlanId : OfpPlanIdentity.PlanId(_ofp.Current);
        }

        var memory = DeiceVerdictMemory.Of(snapshot, planId);

        // Another plan is loaded and the cycle reset that empties the store has not run yet
        // (the OFP part's change comes first): the old leg's answer is not written under it.
        return memory is null || !memory.IsForPlan(OfpPlanIdentity.PlanId(_ofp.Current))
            ? null
            : JsonSerializer.SerializeToNode(memory, FlightStateSnapshotStore.JsonOptions);
    }

    public void Restore(JsonNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var saved = node.Deserialize<DeiceVerdictMemory>(FlightStateSnapshotStore.JsonOptions);
        if (saved is null || string.IsNullOrWhiteSpace(saved.Source))
        {
            return;
        }

        var currentPlanId = OfpPlanIdentity.PlanId(_ofp.Current);
        if (!saved.IsForPlan(currentPlanId))
        {
            _logger.LogInformation(
                "De-icing answer in the flight-state snapshot was given for plan {SavedPlanId}, the plan loaded is {PlanId} — not restored",
                saved.PlanId, currentPlanId);
            return;
        }

        if (!_store.Restore(saved))
        {
            _logger.LogInformation("De-icing answer in the flight-state snapshot not restored — this session already has one");
            return;
        }

        lock (_gate)
        {
            // The observer stamped the plan loaded now; the answer keeps the plan it was given for.
            _answerPlanId = saved.PlanId ?? _answerPlanId;
        }

        var answer = saved.Accepted ? "accepted" : "declined";
        _logger.LogInformation(
            "De-icing answer restored from the flight-state snapshot: {Answer} ({Source}, {AnsweredAtUtc:HH:mm:ss}Z, plan {PlanId}) — GSX's own de-icing offer is answered {OfferAnswer}",
            answer, saved.Source, saved.AnsweredAtUtc, saved.PlanId ?? "none", saved.Accepted ? "Yes" : "No");
        _eventLog.Record(RestoredEvent, new
        {
            answer,
            source = saved.Source,
            reason = saved.Reason,
            answeredAtUtc = saved.AnsweredAtUtc,
            planId = saved.PlanId,
            verdict = saved.Verdict.ToString(),
        });
    }

    private void OnStoreChanged(DeiceRequestSnapshot snapshot)
    {
        var answer = snapshot.Asking.Answer;
        lock (_gate)
        {
            if (answer is null)
            {
                _answer = null;
                _answerPlanId = null;
            }
            else if (!ReferenceEquals(answer, _answer))
            {
                // Stamped once, when the answer appears: a plan loaded later is not its plan.
                _answer = answer;
                _answerPlanId = OfpPlanIdentity.PlanId(_ofp.Current);
            }
        }

        Changed?.Invoke();
    }

    /// <summary>End of duty (ADR-0019 §8): the departure starts over and waits for a new plan —
    /// the old answer goes with it (the file is dropped by the snapshot service itself).</summary>
    private void OnDutyDayEnded(Day.DutyDayEnded ended) => _store.Reset();

    public void Dispose()
    {
        _subscription.Dispose();
        if (_dutyDay is not null)
        {
            _dutyDay.DayEnded -= OnDutyDayEnded;
        }
    }
}
