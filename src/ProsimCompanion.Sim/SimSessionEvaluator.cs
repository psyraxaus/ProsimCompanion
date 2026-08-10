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
public sealed record SimSessionInputs(
    bool Connected,
    bool SimRunning,
    bool Paused,
    int? CameraState,
    bool? IsAvatar);

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

    private int _readyTicks;
    private SimSessionPhase _phase = SimSessionPhase.Unknown;

    public SimSessionPhase ProcessTick(SimSessionInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        if (!inputs.Connected || inputs.CameraState is not int camera)
        {
            _readyTicks = 0;
            return _phase = SimSessionPhase.Unknown;
        }

        var sessionCamera = IsSessionCamera(camera);

        if (_phase is SimSessionPhase.InSession or SimSessionPhase.Walkaround)
        {
            if (!sessionCamera || !inputs.SimRunning)
            {
                _readyTicks = 0;
                return _phase = SimSessionPhase.NotInSession;
            }

            return _phase = Classify(camera, inputs);
        }

        if (sessionCamera && inputs.SimRunning && !inputs.Paused)
        {
            if (++_readyTicks >= EntryDebounceTicks)
            {
                return _phase = Classify(camera, inputs);
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
