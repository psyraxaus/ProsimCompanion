namespace ProsimCompanion.Core.State;

/// <summary>
/// The milestones of one pushback as the GSX side observes them (owner request 2026-11-07,
/// plan V2 — the ramp crew's voice is ours, not SayIntentions'). Raised through
/// <see cref="GroundOpsSignals.PushbackProgress"/> by the pushback tick from the tug LVARs
/// (<c>PUSHBACK_STATUS</c>, <c>VEHICLE_PUSHBACK_STATE</c>) and the beacon sequence's steps;
/// consumed by the pushback crew service, which speaks one line per stage. Edge-only: a stage
/// is raised when the LVAR changes INTO it, never on the first read after a restart.
/// </summary>
public enum PushbackStage
{
    /// <summary>The beacon sequence issued its close-doors step.</summary>
    DoorsClosed,

    /// <summary>The beacon sequence issued its retract-jetway/stairs step.</summary>
    BridgeRemoved,

    /// <summary><c>PUSHBACK_STATUS</c> reached 3/4: the tug is connected, the bypass pin is in.</summary>
    TugConnected,

    /// <summary><c>VEHICLE_PUSHBACK_STATE</c> 8: the push is under way.</summary>
    Pushing,

    /// <summary><c>VEHICLE_PUSHBACK_STATE</c> 11: the tug has stopped.</summary>
    PushComplete,

    /// <summary>The good-engine-start confirmation went in (state 12 answered by the app).</summary>
    EngineStartConfirmed,

    /// <summary><c>VEHICLE_PUSHBACK_STATE</c> 13: the tug is disconnecting.</summary>
    Disconnecting,

    /// <summary><c>VEHICLE_PUSHBACK_STATE</c> 14: clear, the hand signal.</summary>
    Clear,
}
