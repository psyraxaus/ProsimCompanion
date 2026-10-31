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

    /// <summary>The FO's exterior walkaround (issue #196): off by default.</summary>
    public WalkaroundOptions Walkaround { get; set; } = new();

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

/// <summary>
/// The FO's exterior walkaround (issue #196, <c>groundCrew.walkaround</c>): once per departure
/// cycle, at the stand with the ground prep complete, the FO announces the walk, is away for
/// a random few minutes (the captain's queries get "the First Officer is on the walkaround",
/// the refuel call waits) and reports back on the interphone — with, now and then, a finding
/// from <c>walkaround-findings.json</c> that becomes a tech-log item.
/// </summary>
public sealed class WalkaroundOptions
{
    [OptionField(FieldKind.Bool, "speechSettings.groundCrew.walkaround.enabled", Label = "speechSettings.common.enabled")]
    public bool Enabled { get; set; }

    /// <summary>Shortest walk, minutes.</summary>
    [OptionField(FieldKind.Number, "speechSettings.groundCrew.walkaround.minMinutes", Suffix = "min", Min = "1", Max = "30", DependsOn = "Enabled", Advanced = true)]
    public int MinMinutes { get; set; } = 6;

    /// <summary>Longest walk, minutes; the walk is drawn at random between the two.</summary>
    [OptionField(FieldKind.Number, "speechSettings.groundCrew.walkaround.maxMinutes", Suffix = "min", Min = "1", Max = "60", DependsOn = "Enabled", Advanced = true)]
    public int MaxMinutes { get; set; } = 10;

    /// <summary>Probability (0–1) that a walkaround turns up a finding.</summary>
    [OptionField(FieldKind.Number, "speechSettings.groundCrew.walkaround.findingChance", Min = "0", Max = "1", Step = "0.05", DependsOn = "Enabled", Advanced = true)]
    public double FindingChance { get; set; } = 0.15;

    /// <summary>Let a no-go finding be drawn (raised in the tech log regardless; nothing is
    /// blocked — the captain decides).</summary>
    [OptionField(FieldKind.Bool, "speechSettings.groundCrew.walkaround.allowNoGo", DependsOn = "Enabled")]
    public bool AllowNoGo { get; set; }

    /// <summary>The report comes on the interphone (MECH call, INT latch or grace, intercom
    /// filter). Off: spoken in the cockpit unfiltered, for a headset setup where INT is awkward.</summary>
    [OptionField(FieldKind.Bool, "speechSettings.groundCrew.walkaround.onInterphone", DependsOn = "Enabled")]
    public bool OnInterphone { get; set; } = true;
}
