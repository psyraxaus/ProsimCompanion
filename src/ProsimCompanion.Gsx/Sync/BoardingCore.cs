namespace ProsimCompanion.Gsx.Sync;

/// <summary>
/// The boarding/deboarding tick's decision layer, pure (campaign #78): the plan gate on
/// arming (#60) with deferred arm, the write thresholds (boarded-counter change; cargo moves
/// of ≥1 %), the deboard remaining-pax math against GSX's up-counting DEBOARDING_TOTAL, and
/// the unload-percent inversion. The seat-map transformations themselves are already pure and
/// tested (<see cref="ProsimCompanion.Core.Aircraft.SeatMap"/> /
/// <see cref="ProsimCompanion.Core.Aircraft.LoadMath"/>); the shell
/// (<see cref="GsxBoardingSync"/>) owns the writes and their success-tracking.
/// </summary>
internal static class BoardingCore
{
    internal sealed record TickInputs(
        bool BoardingActive,
        bool PendingPlanArm,
        bool DeboardingActive,
        bool Enabled,
        bool DeboardEnabled,
        bool PlanAvailable,
        int BoardedCounter,
        int LastWrittenBoarded,
        double CargoPercent,
        double LastWrittenCargoPct,
        int DeboardCounter,
        int LastWrittenDeboard,
        bool HaveDeboardMap,
        int DeboardStartCount,
        double DeboardCargoPercent,
        double LastWrittenDeboardCargoPct);

    internal sealed record TickPlan(
        bool ArmBoardingNow,
        bool SyncBoardedSeats,
        bool SyncBoardingCargo,
        bool SyncDeboardedSeats,
        int DeboardTargetRemaining,
        bool SyncDeboardCargo,
        double DeboardRemainingCargoPercent);

    /// <summary>Decides what this 1 Hz tick should sync. Deboarding is deliberately never
    /// plan-gated (arrival flow, nothing OFP-derived to latch).</summary>
    internal static TickPlan PlanTick(TickInputs inputs)
    {
        // Deferred arming (#60): boarding went active plan-less and the sync held; the moment
        // the plan arrives, arm normally — the boarded-counter catch-up then seats everyone
        // GSX has already boarded in one update.
        var armNow = inputs.PendingPlanArm && inputs.Enabled && inputs.PlanAvailable;
        var boardingActive = inputs.BoardingActive || armNow;

        var syncBoarded = boardingActive && inputs.Enabled
            && inputs.BoardedCounter >= 0
            && inputs.BoardedCounter != inputs.LastWrittenBoarded;
        var syncBoardCargo = boardingActive && inputs.Enabled
            && Math.Abs(inputs.CargoPercent - inputs.LastWrittenCargoPct) >= 1;

        var syncDeboarded = inputs.DeboardingActive && inputs.DeboardEnabled
            && inputs.DeboardCounter >= 0
            && inputs.DeboardCounter != inputs.LastWrittenDeboard
            && inputs.HaveDeboardMap;
        var syncDeboardCargo = inputs.DeboardingActive && inputs.DeboardEnabled
            && Math.Abs(inputs.DeboardCargoPercent - inputs.LastWrittenDeboardCargoPct) >= 1;

        return new TickPlan(
            ArmBoardingNow: armNow,
            SyncBoardedSeats: syncBoarded,
            SyncBoardingCargo: syncBoardCargo,
            SyncDeboardedSeats: syncDeboarded,
            // Seats empty front-first until remaining = start − deboarded.
            DeboardTargetRemaining: Math.Max(0, inputs.DeboardStartCount - inputs.DeboardCounter),
            SyncDeboardCargo: syncDeboardCargo,
            // DEBOARDING_CARGO_PERCENT counts unload progress up: remaining = 100 − pct.
            DeboardRemainingCargoPercent: 100 - Math.Clamp(inputs.DeboardCargoPercent, 0, 100));
    }

    /// <summary>The Boarding service went Active: latch, or hold for a plan (#60).</summary>
    internal static bool ShouldHoldForPlan(bool requireOfp, bool planAvailable)
        => requireOfp && !planAvailable;
}
