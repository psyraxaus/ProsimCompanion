namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// Cabin-crew simulation (Prosim2FO "Prompt F" semantics): purser reports gated on the ACP CAB
/// receive channel, plus optional ambient events. The report wording lives here too (the
/// predecessor kept it in the SOP profile; this app's convention is one options class per
/// feature). Reports are spoken verbatim — never persona-styled. The distinct purser voice
/// lives in <see cref="VoicesOptions"/>; reports carry the Purser speech role once the
/// call-site wiring lands (WIRING-VOICES.md).
/// </summary>
public sealed class CabinOptions
{
    public const string SectionName = "cabin";

    public bool Enabled { get; set; } = true;

    /// <summary>"Cabin secure" report before takeoff (doors closed + beacon on).</summary>
    public bool CabinSecure { get; set; } = true;

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

    /// <summary>Cabin ding when the application starts (Prosim2GSX's DingOnStartup).</summary>
    public bool DingOnStartup { get; set; }

    /// <summary>Cabin ding when the final loadsheet is transmitted (Prosim2GSX's DingOnFinal).</summary>
    public bool DingOnFinal { get; set; } = true;

    /// <summary>Altitude (ft MSL) below which the "cabin ready" report triggers in Descent/Approach.</summary>
    public double CabinReadyBelowAltFt { get; set; } = 10000;

    /// <summary>Play the interphone ding-dong before cabin calls. (Collapses the predecessor's
    /// dead voices.cabinChime key — one toggle per channel.)</summary>
    public bool Chime { get; set; } = true;

    /// <summary>Optional ambient cabin events (boarding-delay call). Off by default. The
    /// purser cruise query needs the mic-ownership seam and arrives with it.</summary>
    public bool AmbientEvents { get; set; }

    /// <summary>Probability (0–1) of a boarding-delay call at the gate, rolled once per flight.</summary>
    public double BoardingDelayProbability { get; set; } = 0.15;

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
}
