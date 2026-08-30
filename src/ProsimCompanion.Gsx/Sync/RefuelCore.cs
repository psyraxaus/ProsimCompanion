using ProsimCompanion.Core.Aircraft;

namespace ProsimCompanion.Gsx.Sync;

/// <summary>
/// The refuel sync's decision function, pure (campaign #78): target latching (never re-read
/// mid-transfer — ProSim rewrites <c>fuelTarget</c> the moment its refuel session engages),
/// the plan gate on arming (#60), the tankering skip, the defuel guard, hose pause /
/// finish-on-hose, dynamic rate, and completion. The shell (<see cref="GsxRefuelSync"/>)
/// performs the writes and logs the decisions.
/// </summary>
internal static class RefuelCore
{
    internal const double CompletionToleranceKg = 1.0;

    /// <summary>Predecessor FuelCompareVariance: FOB within this of the plan counts as
    /// "already fueled" for the tankering skip.</summary>
    internal const double TankeringToleranceKg = 25.0;

    internal readonly record struct RefuelState(
        bool TransferActive,
        bool PendingPlanArm,
        bool HoseWasConnected,
        bool PumpPowerOn,
        double LatchedTargetKg,
        double DynamicRateKgPerSec,
        bool DivergenceLogged)
    {
        internal static readonly RefuelState Idle = default;
    }

    internal sealed record RefuelInputs(
        bool RequireOfp,
        bool PlanAvailable,
        bool HoseConnected,
        double CurrentKg,
        double FuelTargetRaw,
        double FuelTargetKgRaw,
        double PlannedFuelRaw,
        bool FinishOnHose,
        bool AllowDefuel,
        bool SkipOnTankering,
        string RefuelMethod,
        double FixedRateKgPerSec,
        int TimeTargetSeconds);

    /// <summary>What one evaluation decided. <c>RefuelPower</c> non-null = write the EFB
    /// refuel-power toggle; <c>WriteFuelKg</c> non-null = awaited fuel-quantity write.</summary>
    internal sealed record RefuelOutcome(
        RefuelState State,
        IReadOnlyList<string> Decisions,
        string? Hold = null,
        double? WriteFuelKg = null,
        bool? RefuelPower = null)
    {
        internal static RefuelOutcome Nothing(RefuelState state) => new(state, []);
    }

    /// <summary>The intended TOTAL: <c>aircraft.refuel.fuelTarget</c> (the EFB fuel page's
    /// figure), falling back to <c>efb.plannedfuel</c>, rounded UP to the next 100 kg. The
    /// .kg variant has proven to be a transfer amount and is logged for diagnosis only.</summary>
    private static double ReadTargetKg(RefuelInputs inputs)
        => LoadMath.RoundFuelUpToHundredKg(
            inputs.FuelTargetRaw > 0 ? inputs.FuelTargetRaw : inputs.PlannedFuelRaw);

    /// <summary>The GSX Refueling service went Active. Plan gate on ARMING (#60): GSX-side
    /// requests bypass every app-side gate, and the sync used to latch a target from
    /// stale/absent plan data — it now refuses to latch until a plan exists.</summary>
    internal static RefuelOutcome OnRefuelActive(RefuelState state, RefuelInputs inputs)
    {
        if (inputs.RequireOfp && !inputs.PlanAvailable)
        {
            return new(
                state with { PendingPlanArm = true },
                ["GSX is refueling without a flight plan — sync holds until one arrives "
                    + "(gsx.requireOfpBeforeDeparture); no fuel target latched"]);
        }

        return Activate(state, inputs, []);
    }

    /// <summary>The GSX Refueling service completed. A transfer short of the latched target
    /// (hose pulled early, pauses) snaps the FOB to the target — the predecessor's proven
    /// reconciliation — then refuel power drops.</summary>
    internal static RefuelOutcome OnRefuelCompleted(RefuelState state, RefuelInputs inputs)
    {
        if (state.PendingPlanArm)
        {
            return new(
                state with { PendingPlanArm = false },
                ["GSX refuel completed while holding for a flight plan — no fuel was moved"]);
        }

        if (!state.TransferActive)
        {
            return RefuelOutcome.Nothing(state);
        }

        var target = state.LatchedTargetKg;
        var snap = target > 0 && inputs.CurrentKg < target - CompletionToleranceKg;
        return new(
            state with { TransferActive = false, PumpPowerOn = false },
            [snap
                ? $"GSX reports refuel complete at {inputs.CurrentKg:F0} kg — snapped to latched target {target:F0} kg"
                : $"GSX reports refuel complete at {inputs.CurrentKg:F0} kg"],
            WriteFuelKg: snap ? target : null,
            RefuelPower: false);
    }

    /// <summary>One 1 Hz evaluation while a transfer is active or a plan arm is pending.</summary>
    internal static RefuelOutcome Tick(RefuelState state, RefuelInputs inputs)
    {
        var decisions = new List<string>();

        // Deferred arming (#60): arm the moment the flight plan appears so the mid-service
        // import recovery still works.
        if (state.PendingPlanArm)
        {
            if (!inputs.PlanAvailable)
            {
                return RefuelOutcome.Nothing(state);
            }

            decisions.Add("flight plan arrived mid-service — arming now");
            var armed = Activate(state with { PendingPlanArm = false }, inputs, decisions);
            if (!armed.State.TransferActive)
            {
                return armed; // tankering skip inside the activation
            }
            state = armed.State;
        }

        if (!state.TransferActive)
        {
            return new(state, decisions);
        }

        var hose = inputs.HoseConnected;
        if (hose != state.HoseWasConnected)
        {
            state = state with { HoseWasConnected = hose };

            // Hose pulled mid-transfer: optionally finish instantly at the latched target
            // (predecessor RefuelFinishOnHose) instead of pausing until it reconnects.
            if (!hose && inputs.FinishOnHose && state.LatchedTargetKg > 0)
            {
                decisions.Add($"hose disconnected — finishing instantly at {state.LatchedTargetKg:F0} kg (refuelFinishOnHose)");
                return new(
                    state with { TransferActive = false, PumpPowerOn = false },
                    decisions,
                    WriteFuelKg: state.LatchedTargetKg,
                    RefuelPower: false);
            }

            decisions.Add(hose ? "hose connected" : "hose disconnected — paused");
        }

        if (!hose)
        {
            return PumpOff(state, decisions);
        }

        var current = inputs.CurrentKg;
        if (state.LatchedTargetKg <= 0)
        {
            // Not latched at activation (target not yet written) — keep trying until a real
            // figure appears, but only before any pumping has started.
            var latched = ReadTargetKg(inputs);
            if (latched <= 0)
            {
                return new(state, decisions, Hold: "no fuel target available — waiting");
            }

            state = state with { LatchedTargetKg = latched };
            decisions.Add($"latched target {latched:F0} kg");
            if (ShouldSkipForTankering(inputs, current, latched))
            {
                decisions.Add($"skipped — FOB {current:F0} kg already meets planned {latched:F0} kg (tankering)");
                return new(state with { TransferActive = false }, decisions);
            }
        }

        var target = state.LatchedTargetKg;
        var liveTarget = ReadTargetKg(inputs);
        if (!state.DivergenceLogged && Math.Abs(liveTarget - target) > CompletionToleranceKg)
        {
            state = state with { DivergenceLogged = true };
            decisions.Add(
                $"live fuelTarget changed to {liveTarget:F0} kg mid-transfer — keeping latched {target:F0} kg "
                + "(ProSim rewrites the target while refueling)");
        }

        // Defuel guard: fuel-target datarefs have twice exposed transfer amounts instead of
        // totals. A target below current holds (never pumps down) unless explicitly allowed.
        if (target < current - CompletionToleranceKg && !inputs.AllowDefuel)
        {
            var outcome = PumpOff(state, decisions);
            return outcome with
            {
                Hold = $"target {target:F0} kg is below current {current:F0} kg — defuel disabled (gsx.allowDefuel)",
            };
        }

        var (rate, rateDecision, rateState) = ResolveRate(state, inputs, current, target);
        state = rateState;
        if (rateDecision is not null)
        {
            decisions.Add(rateDecision);
        }

        var next = LoadMath.NextFuelStep(current, target, rate);
        if (Math.Abs(next - current) < 0.01)
        {
            return PumpOff(state, decisions);
        }

        // Fuel is actually about to move: refuel power reflects real pumping only.
        bool? power = null;
        if (!state.PumpPowerOn)
        {
            state = state with { PumpPowerOn = true };
            decisions.Add("pump on — fuel transferring");
            power = true;
        }

        if (Math.Abs(next - target) <= CompletionToleranceKg)
        {
            decisions.Add($"target reached at {target:F0} kg");
            return new(
                state with { TransferActive = false, PumpPowerOn = false },
                decisions,
                WriteFuelKg: next,
                RefuelPower: false);
        }

        return new(state, decisions, WriteFuelKg: next, RefuelPower: power);
    }

    private static RefuelOutcome Activate(RefuelState state, RefuelInputs inputs, List<string> decisions)
    {
        // The target is LATCHED once at activation and never re-read while pumping: ProSim
        // rewrites aircraft.refuel.fuelTarget to the current FOB the moment its refuel
        // session engages (round-4 smoke test: 7317 collapsed to 2500 one tick after
        // pump-on, ending the transfer immediately).
        var latched = ReadTargetKg(inputs);
        state = new RefuelState(
            TransferActive: true,
            PendingPlanArm: false,
            HoseWasConnected: false,
            PumpPowerOn: false,
            LatchedTargetKg: latched,
            DynamicRateKgPerSec: 0,
            DivergenceLogged: false);
        decisions.Add(
            $"activated — current {inputs.CurrentKg:F0} kg; latched target {latched:F0} kg "
            + $"(candidates: fuelTarget {inputs.FuelTargetRaw:F0}, fuelTarget.kg {inputs.FuelTargetKgRaw:F0}, "
            + $"plannedfuel {inputs.PlannedFuelRaw:F0})");

        if (ShouldSkipForTankering(inputs, inputs.CurrentKg, latched))
        {
            decisions.Add($"skipped — FOB {inputs.CurrentKg:F0} kg already meets planned {latched:F0} kg (tankering)");
            state = state with { TransferActive = false };
        }

        return new(state, decisions);
    }

    /// <summary>Tankering skip (predecessor SkipFuelOnTankering): with the FOB already at or
    /// above the plan (within tolerance) the whole transfer is skipped — reached only when GSX
    /// Refueling was called anyway (externally, or with the OFP arriving mid-service); the
    /// sequencer's own pre-call check is <see cref="TankeringSkipReason"/>.</summary>
    private static bool ShouldSkipForTankering(RefuelInputs inputs, double currentKg, double targetKg)
        => inputs.SkipOnTankering
            && targetKg > 0
            && currentKg >= targetKg - TankeringToleranceKg;

    /// <summary>
    /// Pre-call tankering check for the departure sequencer (issue #117): the reason to skip
    /// calling GSX Refueling at all, or null. The 2026-08-29 turnaround carried 9576 kg against
    /// a 7100 kg plan and the truck was still ordered — the in-service skip above only stops
    /// the fuel moving, the crew and hose animation still ran and the sequence waited on it.
    /// Plan figure = the OFP block fuel first (what the pilot means by "above the OFP"), else
    /// the EFB planned-fuel dataref; <c>aircraft.refuel.fuelTarget</c> is deliberately NOT
    /// consulted here — before a refuel session it can still hold the previous leg's value.
    /// </summary>
    internal static string? TankeringSkipReason(
        bool skipOnTankering, bool flightPlanAvailable, double currentKg, double ofpBlockKg, double plannedFuelRaw)
    {
        if (!skipOnTankering)
        {
            return null;
        }

        // No flight plan = no tankering decision (issue #118, 2026-08-30 flight): 90 s after
        // app start this fired on the previous leg's unsettled FOB (9,576 kg) against the
        // EFB dataref's stale 2,300 kg — and refuel then ran anyway once the real 8,000 kg
        // OFP arrived. A skip that retires the Refueling step for the cycle must only be
        // decided from a plan the pilot actually has.
        if (!flightPlanAvailable)
        {
            return null;
        }

        var target = LoadMath.RoundFuelUpToHundredKg(ofpBlockKg > 0 ? ofpBlockKg : plannedFuelRaw);
        return target > 0 && currentKg >= target - TankeringToleranceKg
            ? $"FOB {currentKg:F0} kg already meets planned {target:F0} kg (tankering) — GSX refuel not called"
            : null;
    }

    /// <summary>Per-tick rate. Dynamic method computes it once per transfer from the FIRST
    /// tick's remaining amount over the time target, so the fill takes ~the configured
    /// duration regardless of the ordered quantity; a nonsensical result falls back to the
    /// fixed rate.</summary>
    private static (double Rate, string? Decision, RefuelState State) ResolveRate(
        RefuelState state,
        RefuelInputs inputs,
        double currentKg,
        double targetKg)
    {
        if (!string.Equals(inputs.RefuelMethod, "dynamicRate", StringComparison.OrdinalIgnoreCase))
        {
            return (inputs.FixedRateKgPerSec, null, state);
        }

        if (state.DynamicRateKgPerSec > 0)
        {
            return (state.DynamicRateKgPerSec, null, state);
        }

        var seconds = Math.Max(1, inputs.TimeTargetSeconds);
        var rate = (targetKg - currentKg) / seconds;
        if (rate <= 0)
        {
            rate = inputs.FixedRateKgPerSec;
        }

        return (
            rate,
            $"dynamic rate {rate:F1} kg/s ({targetKg - currentKg:F0} kg over ~{seconds} s)",
            state with { DynamicRateKgPerSec = rate });
    }

    private static RefuelOutcome PumpOff(RefuelState state, List<string> decisions)
    {
        if (!state.PumpPowerOn)
        {
            return new(state, decisions);
        }

        decisions.Add("pump off");
        return new(state with { PumpPowerOn = false }, decisions, RefuelPower: false);
    }
}
