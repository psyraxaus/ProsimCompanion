using ProsimCompanion.Gsx.Automation;

namespace ProsimCompanion.Gsx.Sync;

/// <summary>
/// The single retraction path for the jetway/stairs toggles (issue #225). OperateJetways and
/// OperateStairs are TOGGLES: a second trigger while the first retraction is still playing out
/// re-connects the equipment. Four senders can ask for a retraction within the same seconds —
/// the beacon pushback sequence, the on-final hook, the stairs-after-departure step and the
/// crew's "retract jetway" — so every one of them goes through <see cref="GsxJetwayStairsService"/>,
/// which holds one per-cycle "retraction sent" latch per service in front of the trigger slot.
/// </summary>
public interface IGsxJetwayRetraction
{
    /// <summary>Sends the retraction toggle for <paramref name="serviceId"/> unless one has
    /// already been sent this cycle. The caller decides whether the equipment is connected
    /// (mirror Active/Completed) — this only guards the toggle. Answers
    /// <see cref="GsxTriggerDispatchStatus.Busy"/> naming the same service when the latch
    /// refused it, otherwise the slot's own outcome.</summary>
    Task<GsxTriggerDispatch> RetractAsync(string serviceId, string source, CancellationToken cancellationToken = default);
}
