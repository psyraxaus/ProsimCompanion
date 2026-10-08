using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Flight;

namespace ProsimCompanion.Speech.Cabin;

/// <summary>Which auto-answer window a cabin call fell into (issue #11).</summary>
public enum CabinAutoAnswerGate
{
    /// <summary>Not a gated phase, or the window's option is off: the FO never touches the ACP.</summary>
    None,

    /// <summary>Pushback/engine start or taxi-out — the cabin-secure report's window.</summary>
    Ground,

    /// <summary>Descent or approach — the cabin-ready report's window.</summary>
    Air,
}

/// <summary>What the FO should do on one poll of the call wait.</summary>
public enum CabinCallWaitStep
{
    /// <summary>Keep waiting (poll again).</summary>
    Keep,

    /// <summary>The delay elapsed and nobody answered: perform the answer write now.</summary>
    Answer,

    /// <summary>Play the report: CAB is selected, or the wait is over.</summary>
    Proceed,
}

/// <summary>The auto-answer plan for one cabin call: the window it matched, the configured
/// delay and the dataref the FO will write. Null plan = no auto-answer for this call.</summary>
public sealed record CabinAutoAnswerPlan(CabinAutoAnswerGate Gate, int DelayMs, string DataRef);

/// <summary>
/// Pure decision core of the cabin-call auto-answer (issue #11, Prosim2GSX parity): which
/// phases are answered, after what delay, on which panel, and the poll-by-poll wait machine
/// the shell drives with a clock. The predecessor (ProsimInterface.AnswerCabinCall) pressed
/// the CAB transmission key, then VHF1, then RESET — three momentary presses through a
/// MobiFlight LVAR toggle. Here the answer is ONE idempotent latch write: the FO panel's
/// <c>S_ASP*_CAB_REC_LATCH</c> = 1 (CAB reception selected), which is exactly the condition
/// the purser report already waits for (<see cref="CabinCrewService"/>), so no second
/// "answered" signal has to be invented. The FO's panel is the one opposite the human's seat
/// (speech.pilotSeat, the same seat-relative rule as the flight-control sweep).
/// </summary>
public static class CabinAutoAnswer
{
    /// <summary>Longest the shell waits for ProSim to echo the latch write before treating
    /// the write as swallowed (a hardware panel driving the switch, or a stale subscription).</summary>
    public const int EchoTimeoutMs = 2000;

    /// <summary>The window a call in <paramref name="phase"/> falls into, honouring the two
    /// option switches. Ground = PushbackAndStart or TaxiOut (the cabin-secure report arms on
    /// doors closed + beacon on and often fires while the engines are still starting — the
    /// moment the pilot is busiest, so the push is deliberately inside the window); Air =
    /// Descent or Approach. Everything else — at the gate, climb, cruise, after landing —
    /// is <see cref="CabinAutoAnswerGate.None"/> regardless of the options.</summary>
    public static CabinAutoAnswerGate GateFor(FlightPhase phase, CabinOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return phase switch
        {
            FlightPhase.PushbackAndStart or FlightPhase.TaxiOut when options.AutoAnswerGround => CabinAutoAnswerGate.Ground,
            FlightPhase.Descent or FlightPhase.Approach when options.AutoAnswerAir => CabinAutoAnswerGate.Air,
            _ => CabinAutoAnswerGate.None,
        };
    }

    /// <summary>The FO panel's CAB reception latch for the human's seat: ASP2 (FO) with the
    /// default left seat, ASP1 (captain) when the human flies from the right seat.</summary>
    public static string LatchFor(bool humanIsRightSeat)
        => humanIsRightSeat ? ProsimDataRefNames.Acp1CabLatch.Name : ProsimDataRefNames.Acp2CabLatch.Name;

    /// <summary>Builds the plan for a call, or null when the call is not auto-answered.
    /// A negative delay is clamped to 0.</summary>
    public static CabinAutoAnswerPlan? Plan(FlightPhase phase, CabinOptions options, bool humanIsRightSeat)
    {
        var gate = GateFor(phase, options);
        if (gate == CabinAutoAnswerGate.None)
        {
            return null;
        }

        var delay = gate == CabinAutoAnswerGate.Ground ? options.AutoAnswerGroundDelayMs : options.AutoAnswerAirDelayMs;
        return new CabinAutoAnswerPlan(gate, Math.Max(0, delay), LatchFor(humanIsRightSeat));
    }

    /// <summary>
    /// One poll of the call wait. <paramref name="cabSelected"/> is "CAB reception on any
    /// panel" (a pilot answer or our own echoed write); <paramref name="answerDueAt"/> is
    /// null when there is no plan; <paramref name="answered"/> once the write went out;
    /// <paramref name="waitDeadline"/> is the end of the CAB grace (or the write's echo
    /// window when CAB is not required). The order matters: a selected CAB always wins (the
    /// pilot answered, or the write echoed), then the deadline ends the wait, then the due
    /// answer fires — exactly once.
    /// </summary>
    public static CabinCallWaitStep Step(bool cabSelected, long now, long waitDeadline, long? answerDueAt, bool answered)
    {
        if (cabSelected)
        {
            return CabinCallWaitStep.Proceed;
        }

        if (answerDueAt is { } due && !answered && now >= due)
        {
            return CabinCallWaitStep.Answer;
        }

        return now >= waitDeadline ? CabinCallWaitStep.Proceed : CabinCallWaitStep.Keep;
    }
}
