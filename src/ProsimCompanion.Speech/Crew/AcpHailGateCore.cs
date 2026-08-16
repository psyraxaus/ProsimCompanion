namespace ProsimCompanion.Speech.Crew;

/// <summary>What the transmit gate decided about a spoken hail.</summary>
public enum HailGateDecision
{
    /// <summary>Run the dialogue.</summary>
    Accept,

    /// <summary>Reject AND speak the one-time FO coaching line — first unkeyed hail of
    /// the session only.</summary>
    Coach,

    /// <summary>Reject silently — the pilot has already been coached; an unkeyed hail
    /// simply goes unanswered, like the real interphone.</summary>
    Reject,
}

/// <summary>
/// Pure decision core for the ACP transmit gate (issue #72), extracted from
/// <see cref="CrewHailService"/> so the accept/coach/hangup rules are testable without a
/// microphone, arbiter, or timers. Holds exactly one piece of state: whether the coaching
/// line has been spent this session.
/// </summary>
public sealed class AcpHailGateCore
{
    private bool _coached;

    /// <summary>
    /// Decides whether a spoken hail opens a dialogue.
    /// Unknown transmit state accepts unconditionally — the gate is a realism layer over a
    /// dataref that may not exist (older ProSim, degraded mode), never a way to lose the
    /// feature. The INT-key requirement applies to the INT channel only: the momentary
    /// <c>S_ASP_INT_SEND</c> key IS the INT transmit key; CAB has no equivalent dataref.
    /// </summary>
    /// <param name="gatingEnabled">speech.acpTransmitGating.</param>
    /// <param name="intKeyRequired">speech.acpIntKeyRequired.</param>
    /// <param name="target">Current captain ACP transmit selection.</param>
    /// <param name="intKeyPushed">Whether the momentary INT key is pushed right now.</param>
    /// <param name="requiredChannel">INT for ground hails, CAB for cabin hails.</param>
    public HailGateDecision EvaluateHail(
        bool gatingEnabled,
        bool intKeyRequired,
        AcpTransmitTarget target,
        bool intKeyPushed,
        AcpTransmitTarget requiredChannel)
    {
        if (!gatingEnabled || target == AcpTransmitTarget.Unknown)
        {
            return HailGateDecision.Accept;
        }

        var selectorOk = target == requiredChannel;
        var keyOk = !intKeyRequired || requiredChannel != AcpTransmitTarget.Intercom || intKeyPushed;
        if (selectorOk && keyOk)
        {
            return HailGateDecision.Accept;
        }

        if (_coached)
        {
            return HailGateDecision.Reject;
        }

        _coached = true;
        return HailGateDecision.Coach;
    }

    /// <summary>
    /// Whether an ACTIVE dialogue should end because the transmit selector left the hailed
    /// channel — the "latched to the call" half of the gate. Unknown never hangs up: a
    /// connection dropping mid-dialogue must not slam the phone down on the pilot.
    /// The momentary INT key is deliberately not consulted here — the selector latch keeps
    /// an opened dialogue open.
    /// </summary>
    public static bool ShouldHangUp(
        bool gatingEnabled, AcpTransmitTarget target, AcpTransmitTarget requiredChannel)
        => gatingEnabled
            && target != AcpTransmitTarget.Unknown
            && target != requiredChannel;
}
