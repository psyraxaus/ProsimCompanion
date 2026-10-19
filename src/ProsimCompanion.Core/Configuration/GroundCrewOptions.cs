namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// Ground-crew simulation (ADR-0006 / issue #51): hail dialogues ("cockpit to ground") and
/// crew-initiated upcalls (ground power, chocks, refuel/catering complete) on the INT
/// interphone channel — the mirror of the purser's CAB flow in <see cref="CabinOptions"/>.
/// The distinct ground voice lives in <see cref="VoicesOptions"/>; accent localization in
/// <see cref="AccentOptions"/>.
/// </summary>
public sealed class GroundCrewOptions : IOptionSection
{
    public static string SectionName => "groundCrew";

    [OptionField(FieldKind.Bool, "speechSettings.common.enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Require an ACP INT receive channel (any of the three panels' S_ASP*_INT_REC_LATCH) to
    /// be selected before ground-crew speech plays — like the purser on CAB. Upcalls flash
    /// the MECH call and wait for INT or the grace; hail replies wait the same way (select
    /// INT before calling ground, like real life). When false, speech plays immediately.
    /// </summary>
    [OptionField(FieldKind.Bool, "speechSettings.groundCrew.requireInt", DependsOn = "Enabled")]
    public bool RequireIntChannel { get; set; } = true;

    /// <summary>Grace (s) to wait for INT selection before speaking anyway — a call is never
    /// lost to an unmonitored panel.</summary>
    [OptionField(FieldKind.Number, "speechSettings.groundCrew.intGrace", Suffix = "s", Min = "0", DependsOn = "Enabled; RequireIntChannel", Advanced = true)]
    public int IntChannelGraceSeconds { get; set; } = 25;

    /// <summary>Press the overhead MECH call before an upcall so the ACP lamp flashes — the
    /// flight-deck cue that ground wants to talk (predecessor chocks-flash semantics).</summary>
    [OptionField(FieldKind.Bool, "speechSettings.groundCrew.mechCall", DependsOn = "Enabled")]
    public bool MechCall { get; set; } = true;

    /// <summary>How long the hail listening window stays open for the request.</summary>
    [OptionField(FieldKind.Number, "speechSettings.groundCrew.hailWindow", Suffix = "s", Min = "3", DependsOn = "Enabled", Advanced = true)]
    public int HailListenTimeoutSeconds { get; set; } = 8;

    // ---- Upcalls (each fires once per flight cycle; reset with the ground-ops cycle) ----

    [OptionField(FieldKind.Bool, "speechSettings.groundCrew.upcalls.groundPower", DependsOn = "Enabled")]
    public bool CallOnGroundPower { get; set; } = true;

    [OptionField(FieldKind.Bool, "speechSettings.groundCrew.upcalls.chocks", DependsOn = "Enabled")]
    public bool CallOnChocks { get; set; } = true;

    [OptionField(FieldKind.Bool, "speechSettings.groundCrew.upcalls.refuel", DependsOn = "Enabled")]
    public bool CallOnRefuelComplete { get; set; } = true;

    [OptionField(FieldKind.Bool, "speechSettings.common.cateringComplete", DependsOn = "Enabled")]
    public bool CallOnCateringComplete { get; set; } = true;

    // ---- Wording (spoken verbatim in the GroundCrew role; {fuel} = tonnes on board) ----

    [OptionField(FieldKind.Text, "speechSettings.common.hailReply")]
    public string HailReplyText { get; set; } = "Ground here — go ahead, captain.";

    [OptionField(FieldKind.Text, "speechSettings.groundCrew.wording.standingBy")]
    public string StandingByText { get; set; } = "Standing by.";

    [OptionField(FieldKind.Text, "speechSettings.groundCrew.wording.groundPower")]
    public string GroundPowerText { get; set; } =
        "Cockpit, ground — ground power is connected.";

    [OptionField(FieldKind.Text, "speechSettings.groundCrew.wording.chocks")]
    public string ChocksText { get; set; } = "Cockpit, ground — chocks are in place.";

    [OptionField(FieldKind.Text, "speechSettings.groundCrew.wording.refuelComplete")]
    public string RefuelCompleteText { get; set; } =
        "Cockpit, ground — refueling complete, {fuel} tonnes on board.";

    [OptionField(FieldKind.Text, "speechSettings.common.cateringComplete")]
    public string CateringCompleteText { get; set; } =
        "Cockpit, ground — catering is finished, all service doors closed.";
}
