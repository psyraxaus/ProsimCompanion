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

    [OptionField(FieldKind.Bool, "speechSettings.common.enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>"Cabin secure" report before takeoff (doors closed + beacon on).</summary>
    [OptionField(FieldKind.Bool, "speechSettings.cabin.secure", DependsOn = "Enabled")]
    public bool CabinSecure { get; set; } = true;

    /// <summary>Minimum wait (s) between doors-closed + beacon-on and the "cabin secure"
    /// report (issue #134, Nico 2026-09-27): the crew needs time to arm doors, cross-check
    /// and walk the cabin. 0 restores the instant report.</summary>
    [OptionField(FieldKind.Number, "speechSettings.cabin.secureMinWait", Suffix = "s", Min = "0", DependsOn = "Enabled; CabinSecure")]
    public int CabinSecureMinDelaySeconds { get; set; } = 45;

    /// <summary>Random extra wait per passenger on board (s per pax, drawn once per flight
    /// from 0 to pax × this): a full cabin can still be securing at the holding point, an
    /// empty positioning flight is ready almost at once. Pax = GSX boarded count, else the
    /// OFP figure. 0 disables the pax factor.</summary>
    [OptionField(FieldKind.Number, "speechSettings.cabin.securePerPax", Suffix = "s/pax", Step = "0.1", Min = "0", DependsOn = "Enabled; CabinSecure")]
    public double CabinSecureSecondsPerPax { get; set; } = 1.0;

    /// <summary>"Cabin ready" report on approach (seatbelt signs ON, below the trigger altitude).</summary>
    [OptionField(FieldKind.Bool, "speechSettings.cabin.ready", DependsOn = "Enabled")]
    public bool CabinReady { get; set; } = true;

    /// <summary>The FO gives a short verbatim acknowledgement after each report.</summary>
    [OptionField(FieldKind.Bool, "speechSettings.cabin.foAck", DependsOn = "Enabled")]
    public bool FoAcknowledge { get; set; } = true;

    /// <summary>
    /// Require an ACP CAB receive channel (any of the three panels' S_ASP*_CAB_REC_LATCH) to be
    /// selected before the purser voice plays — the chime rings on the call, the voice waits
    /// until CAB is selected or the grace elapses. When false the voice plays right after the
    /// chime. (The real CAB-CALL light I_ASP_CAB_CALL is read-only in the SDK, so the web UI
    /// banner is the flash substitute.)
    /// </summary>
    [OptionField(FieldKind.Bool, "speechSettings.cabin.requireCab", DependsOn = "Enabled")]
    public bool RequireCabChannel { get; set; } = true;

    /// <summary>Grace (s) to wait for CAB selection before speaking anyway — a report is never lost.</summary>
    [OptionField(FieldKind.Number, "speechSettings.cabin.cabGrace", Suffix = "s", Min = "0", DependsOn = "Enabled; RequireCabChannel", Advanced = true)]
    public int CabChannelGraceSeconds { get; set; } = 25;

    /// <summary>
    /// The FO answers a cabin call on the ground (pushback/engine start and taxi-out — the
    /// cabin-secure window) after <see cref="AutoAnswerGroundDelayMs"/>, by selecting CAB
    /// reception on the FO's audio panel (issue #11, Prosim2GSX's AnswerCabinCallGround). Off
    /// by default (owner decision 2026-10-09): this is the one place the cabin feature writes
    /// to the aircraft. Never answers while a guided dialogue holds the microphone, and never
    /// in any other phase.
    /// </summary>
    [OptionField(FieldKind.Bool, "speechSettings.cabin.autoGround", DependsOn = "Enabled")]
    public bool AutoAnswerGround { get; set; }

    /// <summary>Wait (ms) between the chime and the FO's answer on the ground — the
    /// predecessor's 4000 ms: the FO is busy with the start/taxi.</summary>
    [OptionField(FieldKind.Number, "speechSettings.cabin.groundDelay", Suffix = "ms", Min = "0", Step = "100", DependsOn = "Enabled; AutoAnswerGround")]
    public int AutoAnswerGroundDelayMs { get; set; } = 4000;

    /// <summary>The FO answers a cabin call in descent/approach (the cabin-ready window) after
    /// <see cref="AutoAnswerAirDelayMs"/>. Off by default; same write as the ground answer.</summary>
    [OptionField(FieldKind.Bool, "speechSettings.cabin.autoAir", DependsOn = "Enabled")]
    public bool AutoAnswerAir { get; set; }

    /// <summary>Wait (ms) between the chime and the FO's answer in the air — the
    /// predecessor's 2500 ms.</summary>
    [OptionField(FieldKind.Number, "speechSettings.cabin.airDelay", Suffix = "ms", Min = "0", Step = "100", DependsOn = "Enabled; AutoAnswerAir")]
    public int AutoAnswerAirDelayMs { get; set; } = 2500;

    /// <summary>Cabin ding when the application starts (Prosim2GSX's DingOnStartup).</summary>
    [OptionField(FieldKind.Bool, "speechSettings.cabin.dingStartup")]
    public bool DingOnStartup { get; set; }

    /// <summary>Cabin ding when the final loadsheet is transmitted (Prosim2GSX's DingOnFinal).</summary>
    [OptionField(FieldKind.Bool, "speechSettings.cabin.dingFinal")]
    public bool DingOnFinal { get; set; } = true;

    /// <summary>Altitude (ft MSL) below which the "cabin ready" report triggers in Descent/Approach.</summary>
    [OptionField(FieldKind.Number, "speechSettings.cabin.readyBelow", Suffix = "ft MSL", Min = "1000", DependsOn = "Enabled; CabinReady")]
    public double CabinReadyBelowAltFt { get; set; } = 10000;

    /// <summary>Play the interphone ding-dong before cabin calls. (Collapses the predecessor's
    /// dead voices.cabinChime key — one toggle per channel.)</summary>
    [OptionField(FieldKind.Bool, "speechSettings.cabin.chime", DependsOn = "Enabled")]
    public bool Chime { get; set; } = true;

    /// <summary>Optional ambient cabin events (boarding-delay call). Off by default.</summary>
    [OptionField(FieldKind.Bool, "speechSettings.cabin.ambient", DependsOn = "Enabled")]
    public bool AmbientEvents { get; set; }

    /// <summary>Probability (0–1) of a boarding-delay call at the gate, rolled once per flight.</summary>
    [OptionField(FieldKind.Number, "speechSettings.cabin.boardingDelayProb", Step = "0.05", Min = "0", Max = "1", DependsOn = "Enabled; AmbientEvents")]
    public double BoardingDelayProbability { get; set; } = 0.15;

    // ---- Purser cruise query (Prosim2FO "Prompt F", over the mic-ownership seam) ----

    /// <summary>Once per flight in the cruise the purser calls the flight deck (chime, CAB
    /// latch or grace as for every report) and asks for an arrival-time or turbulence update,
    /// then listens for the captain's reply for <see cref="CruiseQueryWindowSeconds"/>. A
    /// reply naming a time ("about forty minutes", "on time") or the ride ("smooth", "light
    /// chop") earns the matching acknowledgement; anything else "copied, thank you"; silence
    /// "we'll check back later". Off by default like the other ambient cabin events. Never
    /// writes to the aircraft.</summary>
    [OptionField(FieldKind.Bool, "speechSettings.cabin.cruiseQuery", DependsOn = "Enabled")]
    public bool CruiseQuery { get; set; }

    /// <summary>Minutes after the cruise begins before the purser calls, jittered ±30% once
    /// per flight so the call does not land on the same minute every leg.</summary>
    [OptionField(FieldKind.Number, "speechSettings.cabin.cruiseQueryAfter", Suffix = "min", Min = "0", Max = "180", DependsOn = "Enabled; CruiseQuery")]
    public int CruiseQueryDelayMinutes { get; set; } = 10;

    /// <summary>How long the purser listens for the captain's reply, seconds. The mic is
    /// borrowed for exactly this window; a running checklist holds and resumes.</summary>
    [OptionField(FieldKind.Number, "speechSettings.cabin.cruiseQueryWindow", Suffix = "s", Min = "3", Max = "60", DependsOn = "Enabled; CruiseQuery")]
    public int CruiseQueryWindowSeconds { get; set; } = 20;

    // ---- Turbulence, cabin management and the captain's PA (issue #186, November 2026 batch) ----

    /// <summary>Runs the turbulence detector over <c>aircraft.acceleration.Y</c> (100 ms tier):
    /// the ride level, the <c>turbulence.level</c> events and the Flight Status chip. The raw
    /// unit is learned from the at-rest baseline (owner decision 2026-10-24). Off by default;
    /// every reaction below needs it.</summary>
    [OptionField(FieldKind.Bool, "speechSettings.cabin.turbulence.detect", DependsOn = "Enabled")]
    public bool TurbulenceDetection { get; set; }

    /// <summary>The unit of <c>aircraft.acceleration.Y</c>: auto (guessed from the baseline — about
    /// 1 = G, 9.8 = m/s², 32 = ft/s²), or forced when the guess is wrong.</summary>
    [OptionField(FieldKind.Select, "speechSettings.cabin.turbulence.unit", Choices = "turbulenceUnit", DependsOn = "Enabled; TurbulenceDetection", Normalize = FieldNormalization.TrimLower)]
    public string TurbulenceUnit { get; set; } = "auto";

    /// <summary>Deviation from the baseline (G, over a 2 s window) that counts as light
    /// turbulence. The three bands are placeholders until the unit is confirmed live.</summary>
    [OptionField(FieldKind.Number, "speechSettings.cabin.turbulence.light", Suffix = "g", Step = "0.05", Min = "0.05", Max = "3", DependsOn = "Enabled; TurbulenceDetection", Advanced = true)]
    public double TurbulenceLightG { get; set; } = 0.2;

    [OptionField(FieldKind.Number, "speechSettings.cabin.turbulence.moderate", Suffix = "g", Step = "0.05", Min = "0.05", Max = "3", DependsOn = "Enabled; TurbulenceDetection", Advanced = true)]
    public double TurbulenceModerateG { get; set; } = 0.5;

    [OptionField(FieldKind.Number, "speechSettings.cabin.turbulence.severe", Suffix = "g", Step = "0.05", Min = "0.05", Max = "3", DependsOn = "Enabled; TurbulenceDetection", Advanced = true)]
    public double TurbulenceSevereG { get; set; } = 1.0;

    /// <summary>The FO's "seat belt signs, captain?" once per episode at Light or worse while
    /// the sign is not ON. Advice only — the switch is the captain's.</summary>
    [OptionField(FieldKind.Bool, "speechSettings.cabin.turbulence.seatbeltAdvice", DependsOn = "Enabled; TurbulenceDetection")]
    public bool TurbulenceSeatbeltAdvice { get; set; }

    /// <summary>At Moderate the purser calls (chime, CAB latch as for every report) and asks
    /// whether to secure the cabin — a captain's question; yes seats the crew and the cabin
    /// reports secured 30–90 s later. At Severe the crew seats itself and says so. After the
    /// episode the purser reports the cabin back to normal.</summary>
    [OptionField(FieldKind.Bool, "speechSettings.cabin.turbulence.purserCall", DependsOn = "Enabled; TurbulenceDetection")]
    public bool TurbulencePurserCall { get; set; }

    /// <summary>The FO's "captain, your PA" at the usual moments, with a script card on Flight
    /// Status: after take-off, at the top-of-descent notice, and when the seat-belt sign goes on
    /// in cruise turbulence. The captain is a human — nothing reads the PA aloud.</summary>
    [OptionField(FieldKind.Bool, "speechSettings.cabin.pa.prompts", DependsOn = "Enabled")]
    public bool PaPrompts { get; set; }

    /// <summary>Minutes after the OFF edge for the first PA prompt.</summary>
    [OptionField(FieldKind.Number, "speechSettings.cabin.pa.afterTakeoff", Suffix = "min", Min = "1", Max = "60", DependsOn = "Enabled; PaPrompts")]
    public int PaAfterTakeoffMinutes { get; set; } = 10;

    /// <summary>The purser's own cabin PAs, heard over the cabin filter in the purser voice:
    /// arm doors on the beacon-on edge, doors to manual with the engines off on stand, prepare
    /// the cabin for landing at 10,000 ft in the descent. Once each per flight.</summary>
    [OptionField(FieldKind.Bool, "speechSettings.cabin.pa.purserAudible", DependsOn = "Enabled")]
    public bool PurserPaAudible { get; set; }

    // ---- Report wording (predecessor defaults, spoken verbatim in the purser role) ----

    [OptionField(FieldKind.Text, "speechSettings.cabin.wording.secure")]
    public string CabinSecureText { get; set; } =
        "Flight deck, cabin crew. The cabin is secure and ready for departure.";

    [OptionField(FieldKind.Text, "speechSettings.cabin.wording.secureAck")]
    public string CabinSecureAckText { get; set; } = "Thank you, cabin secure.";

    [OptionField(FieldKind.Text, "speechSettings.cabin.wording.ready")]
    public string CabinReadyText { get; set; } =
        "Flight deck, cabin crew. The cabin is secure for landing.";

    [OptionField(FieldKind.Text, "speechSettings.cabin.wording.readyAck")]
    public string CabinReadyAckText { get; set; } = "Thank you, cabin crew.";

    [OptionField(FieldKind.Text, "speechSettings.cabin.wording.boardingDelay")]
    public string BoardingDelayText { get; set; } =
        "Flight deck, cabin crew. We have a short delay — a few passengers still to board.";

    /// <summary>The purser's answer to a "cockpit to crew" hail (ADR-0006 / issue #51).</summary>
    [OptionField(FieldKind.Text, "speechSettings.common.hailReply", Hint = "speechSettings.cabin.wording.hailReplyHint")]
    public string HailReplyText { get; set; } = "Go ahead, captain.";

    /// <summary>The purser's answer to a hail while the cabin-secure timer is still running
    /// (issue #134) — the crew is busy, the report will follow.</summary>
    [OptionField(FieldKind.Text, "speechSettings.cabin.wording.securingReply")]
    public string CabinSecuringReplyText { get; set; } =
        "Still securing the cabin, captain — we'll call you when we're ready.";

    /// <summary>The purser's cruise query (<see cref="CruiseQuery"/>).</summary>
    [OptionField(FieldKind.Text, "speechSettings.common.cruiseQuery", Hint = "speechSettings.cabin.wording.cruiseQueryHint")]
    public string CruiseQueryText { get; set; } =
        "Flight deck, cabin. Any update on arrival time or turbulence for the service?";

    /// <summary>Acknowledgement when the reply named a time or an arrival estimate.</summary>
    [OptionField(FieldKind.Text, "speechSettings.cabin.wording.eta")]
    public string CruiseQueryEtaAckText { get; set; } =
        "Copied, thank you. We'll plan the service around that.";

    /// <summary>Acknowledgement when the reply described the ride.</summary>
    [OptionField(FieldKind.Text, "speechSettings.cabin.wording.ride")]
    public string CruiseQueryRideAckText { get; set; } =
        "Copied, thank you. We'll let the cabin know about the ride.";

    /// <summary>Acknowledgement for any other reply.</summary>
    [OptionField(FieldKind.Text, "speechSettings.cabin.wording.generic")]
    public string CruiseQueryGenericAckText { get; set; } = "Copied, thank you.";

    /// <summary>Spoken when the window closed with no reply.</summary>
    [OptionField(FieldKind.Text, "speechSettings.cabin.wording.noReply")]
    public string CruiseQueryNoReplyText { get; set; } =
        "No worries, we'll check back later.";

    // ---- Turbulence and PA wording (issue #186) ----

    /// <summary>The purser's call at Moderate — the question the captain answers.</summary>
    [OptionField(FieldKind.Text, "speechSettings.cabin.wording.turbulenceCall")]
    public string TurbulenceCallText { get; set; } =
        "Flight deck, cabin. It's getting bumpy back here — should we secure the cabin?";

    /// <summary>The purser's answer to yes.</summary>
    [OptionField(FieldKind.Text, "speechSettings.cabin.wording.turbulenceSeated")]
    public string TurbulenceSeatedAckText { get; set; } =
        "Copied — cabin crew, be seated.";

    /// <summary>The report 30–90 s after yes.</summary>
    [OptionField(FieldKind.Text, "speechSettings.cabin.wording.turbulenceSecured")]
    public string TurbulenceSecuredText { get; set; } =
        "Flight deck, cabin. The cabin is secured.";

    /// <summary>The unprompted report at Severe.</summary>
    [OptionField(FieldKind.Text, "speechSettings.cabin.wording.turbulenceSevere")]
    public string TurbulenceSevereText { get; set; } =
        "Flight deck, cabin. Cabin crew are seated.";

    /// <summary>The report two minutes after the ride settled.</summary>
    [OptionField(FieldKind.Text, "speechSettings.cabin.wording.turbulenceNormal")]
    public string TurbulenceNormalText { get; set; } =
        "Flight deck, cabin. The cabin's back to normal.";

    /// <summary>Purser PA on the beacon-on edge.</summary>
    [OptionField(FieldKind.Text, "speechSettings.cabin.wording.paArmDoors")]
    public string PaArmDoorsText { get; set; } =
        "Cabin crew, arm doors and cross-check.";

    /// <summary>Purser PA with the engines off on stand.</summary>
    [OptionField(FieldKind.Text, "speechSettings.cabin.wording.paDoorsManual")]
    public string PaDoorsManualText { get; set; } =
        "Cabin crew, doors to manual and cross-check.";

    /// <summary>Purser PA at 10,000 ft in the descent.</summary>
    [OptionField(FieldKind.Text, "speechSettings.cabin.wording.paPrepareLanding")]
    public string PaPrepareLandingText { get; set; } =
        "Cabin crew, prepare the cabin for landing.";
}
