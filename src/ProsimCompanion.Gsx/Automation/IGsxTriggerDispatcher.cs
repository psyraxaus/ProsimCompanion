namespace ProsimCompanion.Gsx.Automation;

/// <summary>How an on-demand <c>service.trigger</c> dispatch attempt ended.</summary>
public enum GsxTriggerDispatchStatus
{
    /// <summary>The trigger went out; the automation's confirm/timeout watcher now owns it
    /// (the ack proves nothing — confirmation is the mirror/lifecycle edge).</summary>
    Dispatched,

    /// <summary>The single dispatch slot is occupied by an unconfirmed trigger —
    /// <see cref="GsxTriggerDispatch.BusyServiceId"/> names it when known.</summary>
    Busy,

    /// <summary>GSX definitively rejected the command —
    /// <see cref="GsxTriggerDispatch.RejectCode"/> carries the wire code.</summary>
    Rejected,
}

/// <summary>Outcome of one on-demand dispatch through the serialized trigger slot.</summary>
public sealed record GsxTriggerDispatch(
    GsxTriggerDispatchStatus Status,
    string? BusyServiceId = null,
    string? RejectCode = null);

/// <summary>
/// The single serialized <c>service.trigger</c> dispatch path, implemented by
/// <see cref="GsxAutomationService"/> (which owns the one in-flight slot the departure
/// sequencer uses). On-demand callers (<c>gsx.request*</c> commands, voice) MUST go through
/// this — GSX silently drops rapid-fire triggers, so a second writer is never acceptable.
/// </summary>
public interface IGsxTriggerDispatcher
{
    /// <summary>Attempts to occupy the dispatch slot and send <c>service.trigger</c> for
    /// <paramref name="serviceId"/>. <paramref name="source"/> is decision-logged.</summary>
    Task<GsxTriggerDispatch> TryDispatchServiceTriggerAsync(
        string serviceId,
        string source,
        CancellationToken cancellationToken = default);
}
