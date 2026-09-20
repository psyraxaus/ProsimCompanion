using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Gsx.Sync;

/// <summary>The ground-preparation chain's stages, in owner-specified order. Idle is the
/// resting state (issue #98): before Reposition doubled as "parked, nothing running", every
/// parked publish (reset in climb, hold release after a mid-flight restart) rendered as an
/// active "Repositioning" pill on the Flight Status page for the rest of the flight.</summary>
public enum GsxPrepStage
{
    /// <summary>Chain parked — before the first run of a gate session, and after any reset.</summary>
    Idle,

    Reposition,
    Settling,
    AnchorGate,
    GroundEquipment,
    JetwayStairs,
    Complete,
}

/// <summary>What one prep cycle should do (exactly one of the fields is meaningful).</summary>
internal enum PrepCommand
{
    /// <summary>Preconditions unmet with nothing to say (disabled / GSX not ready / no gate).</summary>
    None,

    /// <summary>Hold with <see cref="PrepStageMachine.PrepDecision.Reason"/> (dedup in the shell).</summary>
    Hold,

    /// <summary>Reset the chain to Reposition with <see cref="PrepStageMachine.PrepDecision.Reason"/>.</summary>
    Reset,

    /// <summary>Run the current stage's step; the shell advances on <see cref="GsxPrepStatus.Done"/>.</summary>
    RunStage,

    /// <summary>The settle window elapsed — advance Settling → AnchorGate.</summary>
    AdvanceFromSettling,
}

/// <summary>
/// The ground-prep coordinator's decision function, pure (campaign #78): hold gates in the
/// exact predecessor-parity order — sim session, startup resync (#30), voice activation
/// (ADR-0006) — then the phase window, readiness and gate-change resets, then which stage
/// step to run. The shell (<see cref="GsxGroundPrepCoordinator"/>) runs the async steps and
/// owns stage advancement on their results.
/// </summary>
internal static class PrepStageMachine
{
    internal sealed record PrepInputs(
        bool AutomationEnabled,
        SimSessionPhase SessionPhase,
        bool ResyncAssessed,
        bool VoiceActivationMode,
        bool CycleStarted,
        FlightPhase FlightPhase,
        bool GsxReady,
        string? GateKey,
        string? SessionGateKey,
        GsxPrepStage Stage,
        DateTimeOffset SettleUntil);

    internal sealed record PrepDecision(PrepCommand Command, string? Reason = null, bool ReleasesHold = false)
    {
        internal static readonly PrepDecision None = new(PrepCommand.None);

        /// <summary>This hold is the GSX-does-not-know-the-parking state (issue #44) — the
        /// shell publishes it as a parking conflict so the Flight Status row AND the FO's
        /// spoken guidance fire, not just a log line (the 2026-08-23 flight held here twice
        /// and the pilot saw and heard nothing).</summary>
        public bool UnknownParking { get; init; }
    }

    internal static PrepDecision Next(PrepInputs inputs, DateTimeOffset now)
    {
        if (!inputs.AutomationEnabled)
        {
            return PrepDecision.None;
        }

        // Predecessor-parity session gate: ProSim pushes plausible cold-and-dark data and the
        // Couatl socket answers while MSFS is still on the main menu or loading. Unknown
        // (SimConnect absent) holds too — a reposition teleports the aircraft, and a signal we
        // cannot read is not a signal that passed. Walkaround holds — services must not be
        // driven while the pilot is outside the aircraft.
        if (inputs.SessionPhase != SimSessionPhase.InSession)
        {
            return new(PrepCommand.Hold, $"MSFS session not active ({inputs.SessionPhase})");
        }

        // Startup-resync ordering (issue #30): the assessment may be about to seed this chain
        // as already complete — prep holds for the verdict; the assessment self-times-out.
        if (!inputs.ResyncAssessed)
        {
            return new(PrepCommand.Hold, "waiting for the startup resync assessment");
        }

        // Voice-gated prep (ADR-0006): the whole chain waits for the pilot to commence ground
        // services. Checked AFTER the resync gate so SeedComplete still fast-forwards a chain
        // that already ran pre-restart.
        if (inputs.Stage != GsxPrepStage.Complete
            && inputs.VoiceActivationMode
            && !inputs.CycleStarted)
        {
            // Pilot-facing (it surfaces on the Flight Status page): no settings-key jargon —
            // "(voice activation mode)" keeps the why without leaking gsx.groundPrepActivation.
            return new(PrepCommand.Hold, "waiting for 'commence ground services' (voice activation mode)");
        }

        if (!inputs.FlightPhase.IsAtGate())
        {
            // Off the ground-prep window; a fresh Preflight after flight restarts the chain.
            // An Idle chain has nothing to reset — it just waits (issue #98: a mid-flight
            // app restart starts at Idle and must stay there quietly).
            if (inputs.Stage != GsxPrepStage.Idle
                && inputs.FlightPhase is FlightPhase.TaxiIn or FlightPhase.Shutdown
                    or FlightPhase.Cruise or FlightPhase.Climb)
            {
                return new(PrepCommand.Reset, $"phase {inputs.FlightPhase}", ReleasesHold: true);
            }
            return new(PrepCommand.None, ReleasesHold: true);
        }

        if (!inputs.GsxReady)
        {
            return new(PrepCommand.None, ReleasesHold: true);
        }

        // GSX is up but has not identified the parking (issue #44, EGLL Stand 547
        // 2026-08-22): GSX believed the aircraft was not on a recognized position, the
        // session gate stayed unknown, and this branch was silent — the pilot restarted the
        // app four times at a state no restart can fix. Pilot-facing wording: the two
        // actions that actually resolve it.
        if (inputs.GateKey is null)
        {
            return new(PrepCommand.Hold,
                "GSX has not identified the parking — select the stand in the GSX menu, or reposition")
            {
                UnknownParking = true,
            };
        }

        // The gate genuinely changed mid/after prep (not the reposition itself settling).
        if (inputs.SessionGateKey is not null
            && !string.Equals(inputs.GateKey, inputs.SessionGateKey, StringComparison.Ordinal)
            && inputs.Stage is GsxPrepStage.JetwayStairs or GsxPrepStage.Complete)
        {
            return new(PrepCommand.Reset, $"gate changed to {inputs.GateKey}", ReleasesHold: true);
        }

        return inputs.Stage switch
        {
            GsxPrepStage.Settling when now >= inputs.SettleUntil
                => new(PrepCommand.AdvanceFromSettling, ReleasesHold: true),
            GsxPrepStage.Settling or GsxPrepStage.Complete
                => new(PrepCommand.None, ReleasesHold: true),
            _ => new(PrepCommand.RunStage, ReleasesHold: true),
        };
    }
}
