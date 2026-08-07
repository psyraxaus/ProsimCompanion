namespace ProsimCompanion.Core.State;

/// <summary>
/// On-demand GSX service calls exposed to UI surfaces (commands, voice, Stream Deck). Each
/// value maps 1:1 to a <c>gsx.request*</c>/<c>gsx.retract*</c> command; the GSX layer decides
/// how (and whether) the action is safe right now.
/// </summary>
public enum GsxServiceAction
{
    RequestRefuel,
    RequestCatering,
    RequestBoarding,
    RequestDeboarding,
    RequestJetway,
    RetractJetway,
    RequestStairs,
    RetractStairs,
    RequestGpu,
    RequestDeice,
    RequestPushback,
}

/// <summary>Classification of one attempted service call — the command layer maps these onto
/// <c>CommandOutcome</c> values, so the split mirrors that model without Core.State depending
/// on Core.Commands.</summary>
public enum GsxServiceCallStatus
{
    /// <summary>The trigger went out through the serialized dispatch slot; confirmation is
    /// observed against the state mirror by the automation layer.</summary>
    Called,

    /// <summary>The requested state already holds (already called/active/completed, or nothing
    /// to retract) — nothing was re-fired.</summary>
    AlreadySatisfied,

    /// <summary>The action is not safe or not possible right now — the detail says why and,
    /// where applicable, what the supported flow is.</summary>
    NotCallable,

    /// <summary>GSX definitively rejected the call.</summary>
    Rejected,

    /// <summary>GSX is not connected/ready (degraded mode).</summary>
    Unavailable,
}

/// <summary>Status plus a human-readable detail — never a bare ack, so remote surfaces can show
/// why nothing visibly happened.</summary>
public sealed record GsxServiceCallOutcome(GsxServiceCallStatus Status, string Detail);

/// <summary>
/// The narrow "call this GSX service now" seam (implemented by the GSX layer). Every call is
/// routed through the SAME serialized service.trigger path the departure automation uses — one
/// trigger in flight at a time, confirmed against the state mirror — never a second writer.
/// </summary>
public interface IGsxServiceControl
{
    /// <summary>Attempts the action. Never throws for operational failures — every outcome is
    /// reported through the record.</summary>
    Task<GsxServiceCallOutcome> TryCallAsync(GsxServiceAction action, CancellationToken cancellationToken = default);
}
