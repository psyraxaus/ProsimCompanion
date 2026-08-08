using ProsimCompanion.Core.State;
using ProsimCompanion.Gsx.Mirror;
using ProsimCompanion.Gsx.Protocol;

namespace ProsimCompanion.Gsx.Services;

/// <summary>
/// Projects a mirrored service plus its lifecycle cycle into the latched display stage. The raw
/// mirror is rebuilt on every frame and has no memory, and GSX flips quick services (and
/// refuel-adjacent behaviour) back to "available" after they finish — so a surface rendering
/// mirror state alone regresses Completed → available (issue #29). The lifecycle latch wins for
/// one-shot services; the jetway/stairs toggles are excluded because their mirror state is
/// positional (docked vs retracted) and a latch would keep reading "connected" after retraction.
/// </summary>
public static class GsxServiceStageProjector
{
    public static GsxServiceStage Project(
        GsxServiceInfo service,
        GsxServiceLifecycleTracker.ServiceCycleSnapshot cycle)
    {
        ArgumentNullException.ThrowIfNull(service);

        // Toggles carry no cycle memory at all: neither the completed latch nor the called
        // fallback — a retracted jetway that was connected (and thus called) earlier in the
        // gate session must read as retracted, not "called" or "completed".
        var latchable = !GsxServiceIds.IsToggle(service.Id);
        if (latchable && cycle.Completed)
        {
            return GsxServiceStage.Completed;
        }

        return service.State switch
        {
            GsxServiceState.Completed => GsxServiceStage.Completed,
            GsxServiceState.Active => GsxServiceStage.Active,
            GsxServiceState.Requested => GsxServiceStage.Requested,
            GsxServiceState.NotAvailable or GsxServiceState.Bypassed => GsxServiceStage.Skipped,
            _ when latchable && cycle.Called => GsxServiceStage.Called,
            _ => GsxServiceStage.Waiting,
        };
    }
}
