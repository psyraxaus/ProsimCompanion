namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// Cabin-crew simulation (Prosim2FO "Prompt F" semantics): purser reports gated on the ACP CAB
/// receive channel, plus optional ambient events. The report wording lives here too (the
/// predecessor kept it in the SOP profile; this app's convention is one options class per
/// feature). Reports are spoken verbatim — never persona-styled. The distinct purser voice
/// lives in <see cref="VoicesOptions"/>; reports carry the Purser speech role once the
/// call-site wiring lands (WIRING-VOICES.md).
/// </summary>
public sealed class CabinOptions : IOptionSection
{
    public static string SectionName => "cabin";

    public bool Enabled { get; set; } = true;

    /// <summary>"Cabin secure" report before takeoff (doors closed + beacon on).</summary>
    public bool CabinSecure { get; set; } = true;

    /// <summary>Minimum wait (s) between doors-closed + beacon-on and the "cabin secure"
    /// report (issue #134, Nico 2026-09-27): the crew needs time to arm doors, cross-check
    /// and walk the cabin. 0 restores the instant report.</summary>
    public int CabinSecureMinDelaySeconds { get; set; } = 45;

    /// <summary>Random extra wait per passenger on board (s per pax, drawn once per flight
    /// from 0 to pax × this): a full cabin can still be securing at the holding point, an
    /// empty positioning flight is ready almost at once. Pax = GSX boarded count, else the
    /// OFP figure. 0 disables the pax factor.</summary>
    public double CabinSecureSecondsPerPax { get; set; } = 1.0;

    /// <summary>"Cabin ready" report on approach (seatbelt signs ON, below the trigger altitude).</summary>
    public bool CabinReady { get; set; } = true;

    /// <summary>The FO gives a short verbatim acknowledgement after each report.</summary>
    public bool FoAcknowledge { get; set; } = true;

    /// <summary>
    /// Require an ACP CAB receive channel (any of the three panels' S_ASP*_CAB_REC_LATCH) to be
    /// selected before the purser voice plays — the chime rings on the call, the voice waits
    /// until CAB is selected or the grace elapses. When false the voice plays right after the
    /// chime. (The real CAB-CALL light I_ASP_CAB_CALL is read-only in the SDK, so the web UI
    /// banner is the flash substitute.)
    /// </summary>
    public bool RequireCabChannel { get; set; } = true;

    /// <summary>Grace (s) to wait for CAB selection before speaking anyway — a report is never lost.</summary>
    public int CabChannelGraceSeconds { get; set; } = 25;

    /// <summary>
    /// The FO answers a cabin call on the ground (pushback/engine start and taxi-out — the
    /// cabin-secure window) after <see cref="AutoAnswerGroundDelayMs"/>, by selecting CAB
    /// reception on the FO's audio panel (issue #11, Prosim2GSX's AnswerCabinCallGround). Off
    /// by default (owner decision 2026-10-09): this is the one place the cabin feature writes
    /// to the aircraft. Never answers while a guided dialogue holds the microphone, and never
    /// in any other phase.
    /// </summary>
    public bool AutoAnswerGround { get; set; }

    /// <summary>Wait (ms) between the chime and the FO's answer on the ground — the
    /// predecessor's 4000 ms: the FO is busy with the start/taxi.</summary>
    public int AutoAnswerGroundDelayMs { get; set; } = 4000;

    /// <summary>The FO answers a cabin call in descent/approach (the cabin-ready window) after
    /// <see cref="AutoAnswerAirDelayMs"/>. Off by default; same write as the ground answer.</summary>
    public bool AutoAnswerAir { get; set; }

    /// <summary>Wait (ms) between the chime and the FO's answer in the air — the
    /// predecessor's 2500 ms.</summary>
    public int AutoAnswerAirDelayMs { get; set; } = 2500;

    /// <summary>Cabin ding when the application starts (Prosim2GSX's DingOnStartup).</summary>
    public bool DingOnStartup { get; set; }

    /// <summary>Cabin ding when the final loadsheet is transmitted (Prosim2GSX's DingOnFinal).</summary>
    public bool DingOnFinal { get; set; } = true;

    /// <summary>Altitude (ft MSL) below which the "cabin ready" report triggers in Descent/Approach.</summary>
    public double CabinReadyBelowAltFt { get; set; } = 10000;

    /// <summary>Play the interphone ding-dong before cabin calls. (Collapses the predecessor's
    /// dead voices.cabinChime key — one toggle per channel.)</summary>
    public bool Chime { get; set; } = true;

    /// <summary>Optional ambient cabin events (boarding-delay call). Off by default.</summary>
    public bool AmbientEvents { get; set; }

    /// <summary>Probability (0–1) of a boarding-delay call at the gate, rolled once per flight.</summary>
    public double BoardingDelayProbability { get; set; } = 0.15;

    // ---- Purser cruise query (Prosim2FO "Prompt F", over the mic-ownership seam) ----

    /// <summary>Once per flight in the cruise the purser calls the flight deck (chime, CAB
    /// latch or grace as for every report) and asks for an arrival-time or turbulence update,
    /// then listens for the captain's reply for <see cref="CruiseQueryWindowSeconds"/>. A
    /// reply naming a time ("about forty minutes", "on time") or the ride ("smooth", "light
    /// chop") earns the matching acknowledgement; anything else "copied, thank you"; silence
    /// "we'll check back later". Off by default like the other ambient cabin events. Never
    /// writes to the aircraft.</summary>
    public bool CruiseQuery { get; set; }

    /// <summary>Minutes after the cruise begins before the purser calls, jittered ±30% once
    /// per flight so the call does not land on the same minute every leg.</summary>
    public int CruiseQueryDelayMinutes { get; set; } = 10;

    /// <summary>How long the purser listens for the captain's reply, seconds. The mic is
    /// borrowed for exactly this window; a running checklist holds and resumes.</summary>
    public int CruiseQueryWindowSeconds { get; set; } = 20;

    // ---- Report wording (predecessor defaults, spoken verbatim in the purser role) ----

    public string CabinSecureText { get; set; } =
        "Flight deck, cabin crew. The cabin is secure and ready for departure.";

    public string CabinSecureAckText { get; set; } = "Thank you, cabin secure.";

    public string CabinReadyText { get; set; } =
        "Flight deck, cabin crew. The cabin is secure for landing.";

    public string CabinReadyAckText { get; set; } = "Thank you, cabin crew.";

    public string BoardingDelayText { get; set; } =
        "Flight deck, cabin crew. We have a short delay — a few passengers still to board.";

    /// <summary>The purser's answer to a "cockpit to crew" hail (ADR-0006 / issue #51).</summary>
    public string HailReplyText { get; set; } = "Go ahead, captain.";

    /// <summary>The purser's answer to a hail while the cabin-secure timer is still running
    /// (issue #134) — the crew is busy, the report will follow.</summary>
    public string CabinSecuringReplyText { get; set; } =
        "Still securing the cabin, captain — we'll call you when we're ready.";

    /// <summary>The purser's cruise query (<see cref="CruiseQuery"/>).</summary>
    public string CruiseQueryText { get; set; } =
        "Flight deck, cabin. Any update on arrival time or turbulence for the service?";

    /// <summary>Acknowledgement when the reply named a time or an arrival estimate.</summary>
    public string CruiseQueryEtaAckText { get; set; } =
        "Copied, thank you. We'll plan the service around that.";

    /// <summary>Acknowledgement when the reply described the ride.</summary>
    public string CruiseQueryRideAckText { get; set; } =
        "Copied, thank you. We'll let the cabin know about the ride.";

    /// <summary>Acknowledgement for any other reply.</summary>
    public string CruiseQueryGenericAckText { get; set; } = "Copied, thank you.";

    /// <summary>Spoken when the window closed with no reply.</summary>
    public string CruiseQueryNoReplyText { get; set; } =
        "No worries, we'll check back later.";
}
