namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// Ground-crew simulation (ADR-0006 / issue #51): hail dialogues ("cockpit to ground") and
/// crew-initiated upcalls (ground power, chocks, refuel/catering complete) on the INT
/// interphone channel — the mirror of the purser's CAB flow in <see cref="CabinOptions"/>.
/// The distinct ground voice lives in <see cref="VoicesOptions"/>; accent localization in
/// <see cref="AccentOptions"/>.
/// </summary>
public sealed class GroundCrewOptions
{
    public const string SectionName = "groundCrew";

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Require an ACP INT receive channel (any of the three panels' S_ASP*_INT_REC_LATCH) to
    /// be selected before ground-crew speech plays — like the purser on CAB. Upcalls flash
    /// the MECH call and wait for INT or the grace; hail replies wait the same way (select
    /// INT before calling ground, like real life). When false, speech plays immediately.
    /// </summary>
    public bool RequireIntChannel { get; set; } = true;

    /// <summary>Grace (s) to wait for INT selection before speaking anyway — a call is never
    /// lost to an unmonitored panel.</summary>
    public int IntChannelGraceSeconds { get; set; } = 25;

    /// <summary>Press the overhead MECH call before an upcall so the ACP lamp flashes — the
    /// flight-deck cue that ground wants to talk (predecessor chocks-flash semantics).</summary>
    public bool MechCall { get; set; } = true;

    /// <summary>How long the hail listening window stays open for the request.</summary>
    public int HailListenTimeoutSeconds { get; set; } = 8;

    // ---- Upcalls (each fires once per flight cycle; reset with the ground-ops cycle) ----

    public bool CallOnGroundPower { get; set; } = true;

    public bool CallOnChocks { get; set; } = true;

    public bool CallOnRefuelComplete { get; set; } = true;

    public bool CallOnCateringComplete { get; set; } = true;

    // ---- Wording (spoken verbatim in the GroundCrew role; {fuel} = tonnes on board) ----

    public string HailReplyText { get; set; } = "Ground here — go ahead, captain.";

    public string StandingByText { get; set; } = "Standing by.";

    public string GroundPowerText { get; set; } =
        "Cockpit, ground — ground power is connected.";

    public string ChocksText { get; set; } = "Cockpit, ground — chocks are in place.";

    public string RefuelCompleteText { get; set; } =
        "Cockpit, ground — refueling complete, {fuel} tonnes on board.";

    public string CateringCompleteText { get; set; } =
        "Cockpit, ground — catering is finished, all service doors closed.";
}
