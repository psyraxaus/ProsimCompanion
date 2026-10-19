using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.State;
using ProsimCompanion.Gsx.Automation;

namespace ProsimCompanion.Gsx.Sync;

/// <summary>Applies one pushback tick effect to its collaborator — the seam the host walks
/// its effect list through.</summary>
internal interface IPushbackTickEffectApplier
{
    void Apply(PushbackTickEffect effect);
}

/// <summary>
/// The pushback tick's effect applier: one effect record → one collaborator call, every
/// ground action fire-and-forget with its own catch (a tick is a timer callback — nothing may
/// escape it). The good-engine-start confirmation is the one effect with an outcome of its
/// own: its success/failure bookkeeping lands on the latches from the menu task.
/// </summary>
public sealed class PushbackTickEffectApplier : IPushbackTickEffectApplier
{
    private readonly IPushbackGroundActions _ground;
    private readonly IGsxTriggerSlot _slot;
    private readonly PushbackTickLatches _latches;
    private readonly GsxDiagnosticsStore _diagnostics;
    private readonly ILogger<GsxPushbackSequenceService> _logger;
    private int _confirming;

    public PushbackTickEffectApplier(
        IPushbackGroundActions ground,
        IGsxTriggerSlot slot,
        PushbackTickLatches latches,
        GsxDiagnosticsStore diagnostics,
        ILogger<GsxPushbackSequenceService> logger)
    {
        ArgumentNullException.ThrowIfNull(ground);
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(latches);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(logger);
        _ground = ground;
        _slot = slot;
        _latches = latches;
        _diagnostics = diagnostics;
        _logger = logger;
    }

    void IPushbackTickEffectApplier.Apply(PushbackTickEffect effect) => Apply(effect);

    /// <summary>Applies one effect (the internal entry the host and tests use).</summary>
    internal void Apply(PushbackTickEffect effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        switch (effect)
        {
            case PushbackDecisionEffect decision:
                RecordDecision(decision.Action, decision.Reason);
                break;
            case SetLastVehicleStateEffect vehicle:
                _latches.SetLastVehicleState(vehicle.Value);
                break;
            case SetLastBypassPinEffect pin:
                _latches.SetLastBypassPin(pin.Value);
                break;
            case ResetSegmentLatchesEffect reset:
                _latches.ResetForSegment(reset.Phase);
                break;
            case SetHookStateEffect hook:
                _latches.SetHookState(hook.State);
                break;
            case CloseDoorsEffect:
                _ = Guard(_ground.CloseAllDoorsAsync, "close doors");
                break;
            case RetractJetwayStairsEffect:
                _ = Guard(_ground.RequestJetwayRemovalAsync, "retract jetway/stairs");
                break;
            case RemoveGroundEquipmentEffect:
                _ = Guard(_ground.RemoveGroundEquipmentForDepartureAsync, "remove ground equipment");
                break;
            case CallPushbackEffect:
                _ = Guard(CallPushbackAsync, "call pushback");
                break;
            case RunDepartureJetwayStepEffect:
                _ = _ground.RunDepartureJetwayStep();
                break;
            case RemoveStairsAfterDepartureEffect stairs:
                _ = Guard(() => _ground.RemoveStairsAfterDepartureAsync(stairs.Mode), "remove stairs after departure");
                break;
            case TickGradualRemovalEffect:
                _ = _ground.TickGradualRemovalAsync();
                break;
            case DecrementConfirmCooldownEffect:
                _latches.DecrementConfirmCooldown();
                break;
            case ConfirmEngineStartEffect:
                TryConfirmEngineStart();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(effect), effect.GetType().Name, "No applier for this pushback tick effect");
        }
    }

    private async Task Guard(Func<Task> action, string name)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pushback sequence action {Action} failed", name);
        }
    }

    /// <summary>Retry-once (campaign #77): pushback was the only sender with NO retry — a
    /// silently dropped call was unrecoverable without pilot action. The slot marks the cycle
    /// called on confirmation, and a failure un-latches the tug path so the next tick
    /// re-offers.</summary>
    private async Task CallPushbackAsync()
    {
        var dispatch = await _slot
            .TryDispatchAsync(new GsxTriggerRequest(PushbackTickGatherer.PushbackServiceId, "pushback sequence")
            {
                RetryOnce = true,
                OnResolved = resolution =>
                {
                    if (resolution != GsxTriggerResolution.Confirmed)
                    {
                        _latches.ClearTugPushbackCalled();
                    }
                },
            })
            .ConfigureAwait(false);
        if (dispatch.Status != GsxTriggerDispatchStatus.Dispatched)
        {
            _latches.ClearTugPushbackCalled();
            RecordDecision(
                "pushback sequence",
                dispatch.Status == GsxTriggerDispatchStatus.Busy
                    ? $"pushback call waiting — trigger slot busy with {dispatch.BusyServiceId ?? "another service"}"
                    : $"pushback trigger rejected ({dispatch.RejectCode})");
        }
    }

    /// <summary>One confirmation attempt at a time; the outcome lands on the latches.</summary>
    private void TryConfirmEngineStart()
    {
        if (Interlocked.Exchange(ref _confirming, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _ground.ConfirmGoodEngineStartAsync(CancellationToken.None).ConfigureAwait(false);

                if (result.Succeeded)
                {
                    _latches.MarkEngineStartConfirmed();
                    RecordDecision("pushback tug", "confirmed good engine start — tug clear to disconnect");
                    return;
                }

                var attempts = _latches.CountConfirmAttempt();
                if (attempts >= PushbackTickEffectPlanner.ConfirmMaxAttempts)
                {
                    _latches.MarkEngineStartConfirmed(); // give up quietly — the menu stays with the pilot
                    RecordDecision(
                        "pushback tug",
                        $"good-engine-start confirm failed {PushbackTickEffectPlanner.ConfirmMaxAttempts} times ({result.Outcome}: {result.Detail}) — leaving the menu to the pilot");
                    return;
                }

                _latches.SetConfirmCooldown(PushbackTickEffectPlanner.ConfirmRetryGapTicks);
                _logger.LogDebug(
                    "Good-engine-start confirm attempt {Attempt} did not land ({Outcome}: {Detail}) — retrying",
                    attempts, result.Outcome, result.Detail);
            }
            catch (Exception ex)
            {
                _latches.SetConfirmCooldown(PushbackTickEffectPlanner.ConfirmRetryGapTicks);
                _logger.LogError(ex, "Good-engine-start confirmation failed");
            }
            finally
            {
                Interlocked.Exchange(ref _confirming, 0);
            }
        });
    }

    private void RecordDecision(string action, string reason)
    {
        _logger.LogInformation("GSX {Action}: {Reason}", action, reason);
        _diagnostics.RecordDecision(new GsxDecisionView(DateTimeOffset.UtcNow, action, reason));
    }
}
