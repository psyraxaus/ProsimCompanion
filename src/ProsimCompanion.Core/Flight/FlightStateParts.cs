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
