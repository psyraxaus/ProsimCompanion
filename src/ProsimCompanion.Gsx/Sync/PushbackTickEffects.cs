using ProsimCompanion.Gsx.Automation;

namespace ProsimCompanion.Gsx.Sync;

/// <summary>One thing the pushback tick does to one collaborator, as a record — the same
/// shape as the departure pump's effects, so the pushback shell is the seam's second adapter.</summary>
internal abstract record PushbackTickEffect;

/// <summary>A decision-log line ("GSX {Action}: {Reason}" + diagnostics row).</summary>
internal sealed record PushbackDecisionEffect(string Action, string Reason) : PushbackTickEffect;

/// <summary>Remember the tug LVAR value just logged.</summary>
internal sealed record SetLastVehicleStateEffect(int Value) : PushbackTickEffect;

/// <summary>Remember the bypass-pin value just logged.</summary>
internal sealed record SetLastBypassPinEffect(double Value) : PushbackTickEffect;

/// <summary>A new flight segment: reset the per-push latches for <paramref name="Phase"/>.</summary>
internal sealed record ResetSegmentLatchesEffect(GsxAutomationPhase Phase) : PushbackTickEffect;

/// <summary>Store the hook core's new state.</summary>
internal sealed record SetHookStateEffect(PushbackTickCore.HookState State) : PushbackTickEffect;

/// <summary>Close every door (the beacon sequence's doors step, or the on-final hook).</summary>
internal sealed record CloseDoorsEffect : PushbackTickEffect;

/// <summary>Retract the jetway / stairs (the sequence step, or the on-final hook).</summary>
internal sealed record RetractJetwayStairsEffect : PushbackTickEffect;

/// <summary>Remove the ground equipment for departure (the sequence's GPU step).</summary>
internal sealed record RemoveGroundEquipmentEffect : PushbackTickEffect;

/// <summary>Call GSX Pushback through the trigger slot (retry-once, campaign #77).</summary>
internal sealed record CallPushbackEffect : PushbackTickEffect;

/// <summary>The departure-phase jetway/stairs connect step (#9).</summary>
internal sealed record RunDepartureJetwayStepEffect : PushbackTickEffect;

/// <summary>Remove the stairs after departure services complete, per the configured mode.</summary>
internal sealed record RemoveStairsAfterDepartureEffect(string Mode) : PushbackTickEffect;

/// <summary>One tick of the non-sequence flow's gradual equipment removal.</summary>
internal sealed record TickGradualRemovalEffect : PushbackTickEffect;

/// <summary>Count down one tick of the good-engine-start retry gap.</summary>
internal sealed record DecrementConfirmCooldownEffect : PushbackTickEffect;

/// <summary>Attempt the "Confirm good engine start" menu pick (issue #106).</summary>
internal sealed record ConfirmEngineStartEffect : PushbackTickEffect;
