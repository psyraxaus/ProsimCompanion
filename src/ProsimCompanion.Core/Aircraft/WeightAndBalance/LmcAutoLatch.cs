using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Aircraft.WeightAndBalance;

/// <summary>What the automatic last-minute-change sequencing decided at one edge.</summary>
public enum LmcAutoAction
{
    /// <summary>Nothing to do and nothing to say (switch off, already evaluated this cycle,
    /// not a departure edge, or the missing final was already noted).</summary>
    None,

    /// <summary>Not evaluated at this edge; the reason is worth one decision line.</summary>
    Wait,

    /// <summary>Run the one automatic evaluation of this departure cycle now.</summary>
    Evaluate,
}

/// <summary>One decision of <see cref="LmcAutoLatch"/>: the action, the edge it was taken at
/// (<see cref="LmcAutoLatch.BoardingCompleteMoment"/> or the pushback stage's name — the
/// <c>moment</c> field of the <c>loadsheet.lmc</c> session event) and the decision-log text.</summary>
public readonly record struct LmcAutoDecision(LmcAutoAction Action, string Moment, string? Reason)
{
    public bool ShouldEvaluate => Action == LmcAutoAction.Evaluate;
}

/// <summary>
/// When the automatic last-minute-change check runs (issue #181, the fault of 2026-11-14).
/// A last-minute change is a change AFTER the final loadsheet was issued, so the check needs a
/// final that is older than the load it is compared with. The final is computed from the live
/// aircraft state at the instant it is generated — comparing at that same instant is always
/// "no change". Two edges therefore, one evaluation per departure cycle, whichever comes first
/// with a final in hand:
/// <list type="bullet">
/// <item>boarding complete — only meaningful when the final was issued by hand BEFORE boarding
/// ended (it was computed from a part-boarded cabin);</item>
/// <item>the departure edge — the first of the pushback stages before the engines run (doors
/// closed, bridge removed, tug connected, pushing): the load can no longer change, and in the
/// standard order (boarding complete → crew-realism delay → final) this is the first moment a
/// final exists that something could have changed against. As first built the check hung off
/// boarding complete alone, the same edge that only SCHEDULES the final, and so never ran.</item>
/// </list>
/// Pure and not thread-safe: the owner serialises the calls.
/// </summary>
public sealed class LmcAutoLatch
{
    /// <summary>The <c>moment</c> of an evaluation taken on the boarding-complete edge.</summary>
    public const string BoardingCompleteMoment = "BoardingComplete";

    private bool _evaluated;
    private bool _missingFinalNoted;

    /// <summary>True once the cycle's automatic evaluation was handed out.</summary>
    public bool Evaluated => _evaluated;

    /// <summary>The pushback stages that count as the departure edge: the ones before the
    /// engines run. The beacon sequence raises DoorsClosed / BridgeRemoved; a pushback asked
    /// for on GSX's own menu raises TugConnected / Pushing only (EGLL 2026-11-14, second
    /// boarding), so any of the four closes the question. The later stages come with engines
    /// running and are no moment to compare weights.</summary>
    public static bool IsDepartureEdge(PushbackStage stage) => stage
        is PushbackStage.DoorsClosed
        or PushbackStage.BridgeRemoved
        or PushbackStage.TugConnected
        or PushbackStage.Pushing;

    /// <summary>Boarding completed. With a final already issued the check runs here; without
    /// one it waits for the departure edge (the delayed final is still to come).</summary>
    public LmcAutoDecision OnBoardingComplete(bool enabled, bool finalExists)
    {
        if (!enabled || _evaluated)
        {
            return new(LmcAutoAction.None, BoardingCompleteMoment, null);
        }

        if (!finalExists)
        {
            return new(LmcAutoAction.Wait, BoardingCompleteMoment,
                "boarding complete — no final loadsheet yet, last-minute change check waits for doors closed / pushback");
        }

        _evaluated = true;
        return new(LmcAutoAction.Evaluate, BoardingCompleteMoment,
            "last-minute change check at boarding complete — the final was issued before boarding ended");
    }

    /// <summary>A pushback stage was raised. The first departure edge with a final in hand
    /// takes the cycle's evaluation; one without a final says so once and leaves the latch
    /// open for the next stage (a final that lands in between is still compared).</summary>
    public LmcAutoDecision OnPushbackStage(PushbackStage stage, bool enabled, bool finalExists)
    {
        var moment = stage.ToString();
        if (!enabled || _evaluated || !IsDepartureEdge(stage))
        {
            return new(LmcAutoAction.None, moment, null);
        }

        if (!finalExists)
        {
            if (_missingFinalNoted)
            {
                return new(LmcAutoAction.None, moment, null);
            }

            _missingFinalNoted = true;
            return new(LmcAutoAction.Wait, moment,
                $"pushback stage {moment} — no final loadsheet this cycle, last-minute change not evaluated");
        }

        _evaluated = true;
        return new(LmcAutoAction.Evaluate, moment,
            $"last-minute change check at pushback stage {moment} — the load aboard against the final loadsheet");
    }

    /// <summary>A new departure cycle.</summary>
    public void Reset()
    {
        _evaluated = false;
        _missingFinalNoted = false;
    }
}
