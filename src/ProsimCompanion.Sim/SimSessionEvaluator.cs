using ProsimCompanion.Core.State;

namespace ProsimCompanion.Sim;

/// <summary>One tick's worth of raw session signals, gathered by <see cref="SimSessionService"/>
/// so the phase logic stays pure and testable without timers or SimConnect.</summary>
/// <param name="Connected">SimConnect handshake completed (true from the MSFS main menu on).</param>
/// <param name="SimRunning">The "Sim" system event's last value.</param>
/// <param name="Paused">Any Pause_EX1 flag set (full, active or sim pause).</param>
/// <param name="CameraState">CAMERA STATE SimVar, or null when never received / stale.</param>
/// <param name="IsAvatar">MSFS 2024 "IS AVATAR" SimVar, null when unavailable (MSFS 2020, or
/// no value yet).</param>
/// <param name="SimQuit">MSFS itself announced that it is closing (SimConnect quit message) —
/// the one disconnect that is known to end the flight.</param>
public sealed record SimSessionInputs(
    bool Connected,
    bool SimRunning,
    bool Paused,
    int? CameraState,
    bool? IsAvatar,
    bool SimQuit = false);

/// <summary>
/// Ports the predecessor's (Prosim2GSX / CFIT.SimConnectLib) empirically-hardened session
/// detection: the pilot is "in the session" only when the camera sits on an in-flight view
/// with the sim state running — the signal the raw SimConnect connection cannot provide,
/// because the handshake succeeds from the main menu.
///
/// Entry additionally requires unpaused: MSFS parks the loaded flight paused on a valid
/// cockpit camera during the "Ready to Fly" hold, and starting automation there was the
/// documented FlowPro failure mode in Prosim2GSX (README §7.4). Entry is debounced over
/// consecutive ticks because the camera flickers through valid values while the flight loads
/// (CFIT latched LastCameraValid over two ticks for the same reason). Once in the session, a
/// pause does NOT end it — only the camera leaving the flight views or the sim state stopping
/// does, matching CFIT's IsSessionStopped.
/// </summary>
public sealed class SimSessionEvaluator
{
    /// <summary>Consecutive ready ticks required to enter the session.</summary>
    public const int EntryDebounceTicks = 2;

    /// <summary>Ticks (500 ms each: 30 s) a live session is held while the SimConnect link is
    /// down. The link's own retry is 5 s, so a drop and reconnect fits several times over; an
    /// MSFS that crashed or was closed without a quit message is given up after this.</summary>
    public const int LinkLossHoldTicks = 60;

    /// <summary>Ticks (3 s) the held session waits, after the link is back, for the "Sim"
    /// system event and the camera to say the flight is still there before the exit counts.</summary>
    public const int ReconnectSettleTicks = 6;

    private int _readyTicks;
    private int _holdTicks;
    private int _settleTicks;
    private SimSessionPhase _phase = SimSessionPhase.Unknown;

    /// <summary>True while a live session is being held across a lost SimConnect link (issue
    /// #246) — the published phase is the one from before the drop.</summary>
    public bool HoldingAcrossLinkLoss { get; private set; }

    public SimSessionPhase ProcessTick(SimSessionInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var live = _phase is SimSessionPhase.InSession or SimSessionPhase.Walkaround;

        if (!inputs.Connected || inputs.CameraState is not int camera)
        {
            _readyTicks = 0;

            // Issue #246 (EGLL 2026-11-15): the app's SimConnect link dropped for five seconds
            // at engine shutdown with MSFS running. The session went Unknown, every
            // session-end teardown ran, and the reconnect's debounce tick read as "the pilot
            // left the flight" — Shutdown → Unknown → Preflight at the stand. A lost link is
            // not evidence that the flight ended: hold the live phase for a bounded time.
            // MSFS saying it quits is evidence, and ends the hold at once.
            if (live && !inputs.SimQuit)
            {
                if (!HoldingAcrossLinkLoss)
                {
                    HoldingAcrossLinkLoss = true;
                    _holdTicks = 0;
                    _settleTicks = 0;
                }

                if (++_holdTicks <= LinkLossHoldTicks)
                {
                    return _phase;
                }
            }

            HoldingAcrossLinkLoss = false;
            return _phase = SimSessionPhase.Unknown;
        }

        var sessionCamera = IsSessionCamera(camera);

        if (live)
        {
            if (!sessionCamera || !inputs.SimRunning)
            {
                // Just after a reconnect the "Sim" event or the camera may not have arrived;
                // a held session gives them a moment before the exit is believed.
                if (HoldingAcrossLinkLoss && ++_settleTicks <= ReconnectSettleTicks)
                {
                    return _phase;
                }

                HoldingAcrossLinkLoss = false;
                _readyTicks = 0;
                return _phase = SimSessionPhase.NotInSession;
            }

            HoldingAcrossLinkLoss = false;
            return _phase = Classify(camera, inputs);
        }

        if (sessionCamera && inputs.SimRunning && !inputs.Paused)
        {
            if (++_readyTicks >= EntryDebounceTicks)
            {
                return _phase = Classify(camera, inputs);
            }

            // Still deciding. From Unknown (first connection, or a link that was down longer
            // than the hold) the answer stays Unknown: NotInSession means the sim was SEEN
            // outside a flight, and the phase engine resets the flight on it (issue #246).
            if (_phase == SimSessionPhase.Unknown)
            {
                return _phase;
            }
        }
        else
        {
            _readyTicks = 0;
        }

        return _phase = SimSessionPhase.NotInSession;
    }

    /// <summary>Camera values that mean a flight session exists. 1–10 are the in-flight views
    /// (cockpit, external, drone, fixed…); 0 is unset, 11 is the load/wait screen, 12+ are the
    /// world map / menu RTCs; 29–31 are MSFS 2024 walkaround-family states (CFIT accepted
    /// 30/31 as session cameras on 2024).</summary>
    private static bool IsSessionCamera(int camera)
        => camera is (> 0 and < 11) or (>= 29 and <= 31);

    /// <summary>Walkaround wins on either signal: the avatar SimVar (authoritative when
    /// present) or the 2024 walkaround camera family. When both IS AVATAR and IS AIRCRAFT
    /// read 1 the predecessor treated the state as transitional and waited — mapping the
    /// ambiguity to Walkaround keeps ground automation held, which is the safe direction.</summary>
    private static SimSessionPhase Classify(int camera, SimSessionInputs inputs)
        => inputs.IsAvatar == true || camera is >= 29 and <= 31
            ? SimSessionPhase.Walkaround
            : SimSessionPhase.InSession;
}
