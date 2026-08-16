namespace ProsimCompanion.Gsx.Automation;

/// <summary>How a <c>service.trigger</c> dispatch attempt ended.</summary>
public enum GsxTriggerDispatchStatus
{
    /// <summary>The trigger went out; the slot's confirm/timeout watcher now owns it
    /// (the ack proves nothing — confirmation is the mirror/lifecycle edge).</summary>
    Dispatched,

    /// <summary>The single dispatch slot stayed occupied by an unconfirmed trigger for the
    /// request's whole slot wait — <see cref="GsxTriggerDispatch.BusyServiceId"/> names it
    /// when known.</summary>
    Busy,

    /// <summary>GSX definitively rejected the command —
    /// <see cref="GsxTriggerDispatch.RejectCode"/> carries the wire code.</summary>
    Rejected,
}

/// <summary>Outcome of one dispatch attempt through the serialized trigger slot.</summary>
public sealed record GsxTriggerDispatch(
    GsxTriggerDispatchStatus Status,
    string? BusyServiceId = null,
    string? RejectCode = null);

/// <summary>How a dispatched (watched) trigger ultimately resolved.</summary>
public enum GsxTriggerResolution
{
    /// <summary>GSX picked the request up (mirror or lifecycle edge) — the service cycle has
    /// been marked called.</summary>
    Confirmed,

    /// <summary>No confirming edge within the confirm window (after any automatic retry) —
    /// GSX silently dropped the call.</summary>
    Dropped,

    /// <summary>GSX rejected the send (initial or the automatic retry) — the slot is free
    /// again immediately.</summary>
    Rejected,
}

/// <summary>
/// One <c>service.trigger</c> request. The per-request knobs exist because the senders
/// genuinely differ (2026-08 inventory): the departure sequencer re-offers dropped services
/// itself (no watcher retry), on-demand and pushback calls want one automatic retry (#76),
/// the jetway/stairs connect wants a long window and a cross-service fallback instead of a
/// re-send, and toggle removals must not be watched at all (a re-fire would re-connect).
/// </summary>
public sealed record GsxTriggerRequest(string ServiceId, string Source)
{
    /// <summary>Confirm window; null uses <c>GsxOptions.TriggerConfirmTimeoutMs</c>.</summary>
    public TimeSpan? ConfirmWindow { get; init; }

    /// <summary>One automatic re-send after a silent drop (issue #76 semantics).</summary>
    public bool RetryOnce { get; init; }

    /// <summary>Send without watching: the trigger is a toggle whose confirming edge is the
    /// service LEAVING Active/Completed (a retraction), so the connect predicate would lie.
    /// The slot is still occupied for a short spacing interval — GSX silently drops
    /// rapid-fire triggers.</summary>
    public bool NoConfirm { get; init; }

    /// <summary>How long the dispatch may wait for the slot to free before giving up with
    /// <see cref="GsxTriggerDispatchStatus.Busy"/>. Zero = strict try-acquire.</summary>
    public TimeSpan SlotWait { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Invoked exactly once when a watched trigger resolves (never for
    /// <see cref="NoConfirm"/> sends). Runs on the watcher task — keep it cheap and never
    /// let it throw.</summary>
    public Action<GsxTriggerResolution>? OnResolved { get; init; }
}

/// <summary>
/// The single serialized <c>service.trigger</c> path — the "trigger slot" (CONTEXT.md). GSX
/// silently drops rapid-fire triggers, so exactly one trigger may be in flight at a time and
/// this module is the only code allowed to build the wire payload (enforced by analyzer
/// PCGSX001 + a source guard test). Every sender — sequencer, on-demand commands, arrival
/// deboard, pushback, jetway/stairs — goes through here.
/// </summary>
public interface IGsxTriggerSlot
{
    /// <summary>Service id of the trigger currently in flight, or null when the slot is free.
    /// The departure sequencer feeds this into its plan; status boards render it as Called.</summary>
    string? InFlightServiceId { get; }

    /// <summary>Raised whenever the slot's occupancy changes (dispatch, confirm, drop,
    /// rejection, reset) — the automation pump re-evaluates on it. Raised on watcher/caller
    /// threads.</summary>
    event Action? Changed;

    /// <summary>Attempts to occupy the slot and send <c>service.trigger</c> for the request's
    /// service. See <see cref="GsxTriggerRequest"/> for the per-request policy.</summary>
    Task<GsxTriggerDispatch> TryDispatchAsync(GsxTriggerRequest request, CancellationToken cancellationToken = default);

    /// <summary>Clears any in-flight trigger without resolving it (its watcher exits on the
    /// next poll). Used at the arrival cycle boundary.</summary>
    void Reset(string reason);
}
