using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Flight;

namespace ProsimCompanion.Speech.Cabin;

/// <summary>What the cabin decided to do on one tick.</summary>
public enum CabinAction
{
    None,

    /// <summary>The crew started securing (doors closed + beacon on, pushback/taxi-out):
    /// the randomized timer is armed — <see cref="CabinCrewCore.SecureDelaySeconds"/> says
    /// for how long (issue #134). The report itself follows as <see cref="SecureReport"/>.</summary>
    SecureArmed,

    /// <summary>"Cabin secure" report (doors closed + beacon on, pushback/taxi-out).</summary>
    SecureReport,

    /// <summary>"Cabin ready" report (signs ON, below the trigger altitude in descent/approach).</summary>
    ReadyReport,

    /// <summary>Ambient boarding-delay call at the gate (opt-in, one dice roll per flight).</summary>
    BoardingDelay,

    /// <summary>The purser's once-per-flight cruise query ("any update on arrival time or
    /// turbulence?") with its reply window — due now; the shell runs the dialogue.</summary>
    CruiseQuery,
}

/// <summary>Inputs for one cabin tick — sampled by the shell, judged here.
/// <paramref name="HasBeenAirborne"/> is the engine's airborne-this-session latch: the
/// landing report is meaningless without a flight having actually happened (issue #59 — a
/// bogus startup Approach classification fired "secure for landing" at the gate).
/// <paramref name="PaxOnBoard"/> scales the cabin-secure wait (issue #134); <paramref name="NowUtc"/>
/// is the tick's clock so the timer is testable without waiting — default means "use the
/// wall clock". <paramref name="VoicePaused"/> is the pilot's "ear off" latch: the cruise
/// query waits it out rather than calling a flight deck that cannot answer.</summary>
public sealed record CabinTickSample(
    FlightPhase Phase,
    bool DoorsClosed,
    bool BeaconOn,
    int SeatbeltSignsMode,
    double AltitudeFt,
    double VerticalSpeedFpm = 0,
    bool HasBeenAirborne = false,
    int PaxOnBoard = 0,
    DateTimeOffset NowUtc = default,
    bool VoicePaused = false);

/// <summary>
/// The pure once-per-flight trigger logic of the cabin-crew simulation (Prosim2FO semantics,
/// with the re-arm widened: the predecessor only re-armed at ColdAndDark, so a turnaround that
/// never went cold got no second set of reports — here a fresh Preflight after Shutdown/TaxiIn
/// re-arms too). "Cabin ready" is deliberately a level test, not a descending-through edge, so
/// a short hop that never climbs above the trigger altitude still gets its report. Seatbelt
/// signs must be ON (1) — AUTO (0) does not trigger (predecessor parity, S_OH_SIGNS 3-state).
/// <para>
/// "Cabin secure" (issue #134, Nico 2026-09-27) is no longer instant: the first tick with
/// doors closed + beacon on arms a timer of <c>min + random(0 … pax × perPax)</c> seconds,
/// drawn once per flight, and the report fires when it expires — provided the doors are
/// still closed and the beacon still on at that moment (a re-opened door or a paused push
/// holds the report, it never cancels the timer). So the cabin is sometimes ready right
/// after pushback and sometimes still securing at the holding point, like a real crew.
/// </para>
/// <para>
/// The purser cruise query (Prosim2FO "Prompt F", 2026-10-09) is armed on the first cruise
/// tick with a jittered delay drawn once per flight, and fires on the first cruise tick at or
/// after that moment with the pilot's ear on. A step climb between the two is harmless — the
/// timer keeps running and the call waits for the next cruise tick. The query is only ever
/// made in the cruise: a descent before it is due means no call this flight.
/// </para>
/// </summary>
public sealed class CabinCrewCore
{
    /// <summary>How long the shell's retry waits when the mic was busy at the due moment.</summary>
    public static readonly TimeSpan CruiseQueryRetry = TimeSpan.FromSeconds(60);

    private bool _secureDone;
    private bool _secureArmed;
    private DateTimeOffset _secureDueAtUtc;
    private bool _readyDone;
    private bool _boardingRolled;
    private bool _cruiseQueryDone;
    private bool _cruiseQueryArmed;
    private DateTimeOffset _cruiseQueryDueAtUtc;

    /// <summary>The wait drawn for this flight's cabin-secure report (0 until armed).</summary>
    public int SecureDelaySeconds { get; private set; }

    /// <summary>When the running cabin-secure timer expires; null when not armed or already
    /// reported.</summary>
    public DateTimeOffset? SecureDueAtUtc => _secureArmed && !_secureDone ? _secureDueAtUtc : null;

    /// <summary>The delay drawn for this flight's purser cruise query (0 until armed).</summary>
    public int CruiseQueryDelaySeconds { get; private set; }

    /// <summary>When the purser will call; null when not armed or already called.</summary>
    public DateTimeOffset? CruiseQueryDueAtUtc => _cruiseQueryArmed && !_cruiseQueryDone ? _cruiseQueryDueAtUtc : null;

    public void OnPhaseChanged(FlightPhase from, FlightPhase to)
    {
        if (to == FlightPhase.ColdAndDark
            || (to == FlightPhase.Preflight && from is FlightPhase.Shutdown or FlightPhase.TaxiIn))
        {
            _secureDone = false;
            _secureArmed = false;
            SecureDelaySeconds = 0;
            _readyDone = false;
            _boardingRolled = false;
            _cruiseQueryDone = false;
            _cruiseQueryArmed = false;
            CruiseQueryDelaySeconds = 0;
        }
    }

    /// <summary>The cruise-query delay for one flight: the configured minutes, ±30% from the
    /// 0–1 <paramref name="roll"/>. A negative setting reads as 0 (call at once).</summary>
    public static int CruiseQueryDelay(CabinOptions options, double roll)
    {
        ArgumentNullException.ThrowIfNull(options);
        var minutes = Math.Max(0, options.CruiseQueryDelayMinutes);
        var factor = 0.7 + 0.6 * Math.Clamp(roll, 0, 1);
        return (int)Math.Round(minutes * 60 * factor);
    }

    /// <summary>The shell found the microphone borrowed by another dialogue at the due
    /// moment: the once-per-flight latch is handed back and the call retried a minute later,
    /// so a tech-log entry or a hail in progress never costs the flight its purser call.</summary>
    public void ReopenCruiseQuery(DateTimeOffset nowUtc)
    {
        _cruiseQueryDone = false;
        _cruiseQueryArmed = true;
        _cruiseQueryDueAtUtc = nowUtc + CruiseQueryRetry;
    }

    /// <summary>The cabin-secure wait for one flight: the configured minimum plus a random
    /// share (<paramref name="roll"/> 0–1) of pax × seconds-per-pax. Negative settings and a
    /// missing pax count read as 0, so a misconfigured file degrades to the instant report.</summary>
    public static int SecureDelay(CabinOptions options, int paxOnBoard, double roll)
    {
        ArgumentNullException.ThrowIfNull(options);

        var minimum = Math.Max(0, options.CabinSecureMinDelaySeconds);
        var perPax = Math.Max(0, options.CabinSecureSecondsPerPax);
        var spread = Math.Clamp(roll, 0, 1) * Math.Max(0, paxOnBoard) * perPax;
        return (int)Math.Round(minimum + spread);
    }

    /// <summary>Judges one tick. <paramref name="roll"/> supplies the 0–1 dice value for the
    /// ambient event and the cabin-secure spread (injected so tests are deterministic). A
    /// losing roll still consumes the flight's one boarding-delay chance — "probability per
    /// flight", predecessor parity.</summary>
    public CabinAction Evaluate(CabinTickSample sample, CabinOptions options, Func<double> roll)
    {
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(roll);

        if (options.CabinSecure && !_secureDone
            && sample.Phase is FlightPhase.PushbackAndStart or FlightPhase.TaxiOut
            && sample.DoorsClosed && sample.BeaconOn)
        {
            var now = sample.NowUtc == default ? DateTimeOffset.UtcNow : sample.NowUtc;
            if (!_secureArmed)
            {
                _secureArmed = true;
                SecureDelaySeconds = SecureDelay(options, sample.PaxOnBoard, roll());
                _secureDueAtUtc = now.AddSeconds(SecureDelaySeconds);
                if (SecureDelaySeconds > 0)
                {
                    return CabinAction.SecureArmed;
                }
            }

            if (now >= _secureDueAtUtc)
            {
                _secureDone = true;
                return CabinAction.SecureReport;
            }
        }

        // VS guard (issue #48): a phase blip to Approach during a climb must not trigger the
        // landing report — the cabin only reports ready while genuinely not climbing away.
        // Airborne guard (issue #59): a bogus startup Approach classification fired "secure
        // for landing" on a cold aircraft at the gate — the report requires an actual flight.
        if (options.CabinReady && !_readyDone
            && sample.HasBeenAirborne
            && sample.Phase is FlightPhase.Descent or FlightPhase.Approach
            && sample.VerticalSpeedFpm < 300
            && sample.SeatbeltSignsMode == 1
            && sample.AltitudeFt > 0
            && sample.AltitudeFt <= options.CabinReadyBelowAltFt)
        {
            _readyDone = true;
            return CabinAction.ReadyReport;
        }

        if (options.AmbientEvents && !_boardingRolled
            && sample.Phase is FlightPhase.Preflight or FlightPhase.Departure or FlightPhase.PushbackAndStart)
        {
            _boardingRolled = true;
            if (roll() < Math.Clamp(options.BoardingDelayProbability, 0, 1))
            {
                return CabinAction.BoardingDelay;
            }
        }

        if (options.CruiseQuery && !_cruiseQueryDone && sample.Phase == FlightPhase.Cruise)
        {
            var now = sample.NowUtc == default ? DateTimeOffset.UtcNow : sample.NowUtc;
            if (!_cruiseQueryArmed)
            {
                _cruiseQueryArmed = true;
                CruiseQueryDelaySeconds = CruiseQueryDelay(options, roll());
                _cruiseQueryDueAtUtc = now.AddSeconds(CruiseQueryDelaySeconds);
            }

            // The ear-off latch holds the call, never cancels it: the purser calls on the
            // first cruise tick after the pilot can hear and answer again.
            if (now >= _cruiseQueryDueAtUtc && !sample.VoicePaused)
            {
                _cruiseQueryDone = true;
                return CabinAction.CruiseQuery;
            }
        }

        return CabinAction.None;
    }
}
