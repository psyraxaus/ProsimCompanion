using ProsimCompanion.Gsx.Automation;

namespace ProsimCompanion.Gsx.Sync;

/// <summary>
/// The arrival tick's decision function, pure (campaign #78): stable-parked detection, the
/// Shutdown→Preparation straddle (the phase engine flips fast once parked — the arrival keeps
/// processing until its actions have run), the chock countdown with its abort-on-movement
/// rule, and the arrival-actions transition. The shell (<see cref="GsxArrivalService"/>) owns
/// the effects: FOB save, pax arming, deboard dispatch, jetway step, equipment placement.
/// </summary>
internal static class ArrivalCore
{
    /// <summary>Carry-over latches between ticks. <c>ChockCountdown</c> −1 = inactive.</summary>
    internal readonly record struct ArrivalState(
        int StableSeconds,
        bool ArrivalHandled,
        bool WasInArrivalPhases,
        int ChockCountdown)
    {
        internal static readonly ArrivalState Initial = new(0, false, false, -1);
    }

    internal sealed record ArrivalInputs(
        bool Enabled,
        GsxAutomationPhase Phase,
        bool SnapshotValid,
        bool OnGround,
        bool AnyEngineRunning,
        bool ParkBrakeSet,
        double GroundSpeedKt,
        bool BeaconOn,
        int ArrivalStableSecondsOption,
        bool AutoCallDeboard,
        bool DeboardAlreadyCalled,
        bool AutoGroundEquipment,
        int ChockDelayMinSec,
        int ChockDelayMaxSec);

    internal sealed record ArrivalTickOutcome(
        ArrivalState State,
        bool ResetDeboardLatch,
        bool RunJetwayArrivalStep,
        bool TryCallDeboard,
        bool RunArrivalActions,
        int? ChockCountdownStarted,
        bool ChockAborted,
        bool PlaceChocks,
        bool TryRestoreFob);

    /// <summary>One 1 Hz evaluation. <paramref name="chockRoll"/> supplies the randomized
    /// chock delay (min, exclusive-max) → seconds — injected so tests are deterministic.</summary>
    internal static ArrivalTickOutcome Tick(
        ArrivalState state,
        ArrivalInputs inputs,
        Func<int, int, int> chockRoll)
    {
        var none = new ArrivalTickOutcome(state, false, false, false, false, null, false, false, false);
        if (!inputs.Enabled)
        {
            return none;
        }

        var inArrivalPhases = inputs.Phase is GsxAutomationPhase.TaxiIn or GsxAutomationPhase.Arrival;

        // A fresh arrival segment resets the arrival latches.
        var resetDeboard = false;
        if (inArrivalPhases && !state.WasInArrivalPhases)
        {
            state = new ArrivalState(0, false, state.WasInArrivalPhases, -1);
            resetDeboard = true;
        }

        // The phase engine flips Shutdown -> Preflight quickly once parked (turnaround);
        // keep processing the arrival until its actions have actually run.
        var pendingArrival = state.WasInArrivalPhases && !state.ArrivalHandled
            && inputs.Phase == GsxAutomationPhase.Preparation;
        state = state with { WasInArrivalPhases = inArrivalPhases || pendingArrival };

        if (!inArrivalPhases && !pendingArrival)
        {
            return none with
            {
                State = state,
                ResetDeboardLatch = resetDeboard,
                TryRestoreFob = inputs.Phase == GsxAutomationPhase.Preparation,
            };
        }

        var stable = inputs.SnapshotValid && inputs.OnGround && !inputs.AnyEngineRunning
            && inputs.ParkBrakeSet && inputs.GroundSpeedKt < 2.0 && !inputs.BeaconOn;
        state = state with { StableSeconds = stable ? state.StableSeconds + 1 : 0 };

        if (state.ArrivalHandled)
        {
            // Jetway/stairs first (predecessor order: gate path before pax leave), then
            // deboarding — both keep retrying until they succeed or give up.
            var outcome = none with
            {
                State = state,
                ResetDeboardLatch = resetDeboard,
                RunJetwayArrivalStep = true,
                TryCallDeboard = !inputs.DeboardAlreadyCalled && inputs.AutoCallDeboard,
            };

            if (state.ChockCountdown < 0)
            {
                return outcome;
            }

            // Movement mid-countdown aborts the placement outright (predecessor rule:
            // "parked state no longer stable").
            if (!stable)
            {
                return outcome with
                {
                    State = state with { ChockCountdown = -1 },
                    ChockAborted = true,
                };
            }

            var remaining = state.ChockCountdown - 1;
            return remaining <= 0
                ? outcome with { State = state with { ChockCountdown = -1 }, PlaceChocks = true }
                : outcome with { State = state with { ChockCountdown = remaining } };
        }

        if (state.StableSeconds < Math.Max(1, inputs.ArrivalStableSecondsOption))
        {
            return none with { State = state, ResetDeboardLatch = resetDeboard };
        }

        state = state with { ArrivalHandled = true };
        int? chockStart = null;
        if (inputs.AutoGroundEquipment)
        {
            // Randomized chock delay (predecessor ChockDelayMin/Max): the ground crew takes a
            // human moment to walk the chocks out after shutdown.
            var lo = Math.Max(0, inputs.ChockDelayMinSec);
            var hi = Math.Max(lo + 1, inputs.ChockDelayMaxSec);
            chockStart = chockRoll(lo, hi);
            state = state with { ChockCountdown = chockStart.Value };
        }

        return none with
        {
            State = state,
            ResetDeboardLatch = resetDeboard,
            RunArrivalActions = true,
            TryCallDeboard = inputs.AutoCallDeboard,
            ChockCountdownStarted = chockStart,
        };
    }
}
