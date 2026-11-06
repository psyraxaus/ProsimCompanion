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

    /// <summary>"Fuelling in progress, N tonnes requested" as GSX Refueling goes Active (owner
    /// request 2026-11-07, plan V3).</summary>
    [OptionField(FieldKind.Bool, "speechSettings.groundCrew.upcalls.refuelStarted", DependsOn = "Enabled")]
    public bool CallOnRefuelStarted { get; set; } = true;

    /// <summary>"De-icing complete, Type four — holdover time starts now" at the GSX de-icing
    /// completed edge (owner request 2026-11-07, plan V3).</summary>
    [OptionField(FieldKind.Bool, "speechSettings.groundCrew.upcalls.deiceComplete", DependsOn = "Enabled")]
    public bool CallOnDeiceComplete { get; set; } = true;

    /// <summary>The FO's exterior walkaround (issue #196): off by default.</summary>
    public WalkaroundOptions Walkaround { get; set; } = new();

    /// <summary>The pushback crew's voice (owner request 2026-11-07, plan V2): on by default, the
    /// ramp agent talks the push through on the interphone.</summary>
    public PushbackCrewOptions PushbackCrew { get; set; } = new();

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

    /// <summary><c>{fuel}</c> = the planned block fuel in tonnes (INIT override, else the OFP);
    /// the clause drops when no plan is known.</summary>
    [OptionField(FieldKind.Text, "speechSettings.groundCrew.wording.refuelStarted")]
    public string RefuelStartedText { get; set; } =
        "Cockpit, ground — fuelling in progress, {fuel} tonnes requested.";

    /// <summary><c>{fluid}</c> = "Type one" … "Type four" from GSX's fluid enum; the clause
    /// drops when the type is unknown.</summary>
    [OptionField(FieldKind.Text, "speechSettings.groundCrew.wording.deiceComplete")]
    public string DeiceCompleteText { get; set; } =
        "Cockpit, ground — de-icing complete, {fluid}. Holdover time starts now.";
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

/// <summary>
/// The pushback crew (owner request 2026-11-07, plan V2, <c>groundCrew.pushbackCrew</c>): the
/// ramp agent on the interphone from the bridge coming off to the hand signal — bridge removed
/// and doors checked, tug connected with the brakes-release request, the push clearance with
/// the direction of this flight, the engine-start clearance, push complete, good start
/// confirmed, pin out and clear. One line per <see cref="Core.State.PushbackStage"/>, each once
/// per push; the GroundCrew role (voice, accent, intercom filter) and the INT gating of the
/// upcalls. Lines are spoken verbatim; <c>{direction}</c> becomes "tail left" / "tail right" /
/// "straight back" / the stand's slot label, or is dropped with its comma when unknown.
/// </summary>
public sealed class PushbackCrewOptions
{
    [OptionField(FieldKind.Bool, "speechSettings.groundCrew.pushbackCrew.enabled", Label = "speechSettings.common.enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>Ask "release parking brake" when the tug connects and wait for the captain's
    /// "brakes released" before the push clearance; off: the crew reports the tug and the
    /// clearance comes with the push itself.</summary>
    [OptionField(FieldKind.Bool, "speechSettings.groundCrew.pushbackCrew.brakesCheck", DependsOn = "Enabled")]
    public bool BrakesCheck { get; set; } = true;

    /// <summary>Clear the engine start as the push begins (the A320 starts during the push).</summary>
    [OptionField(FieldKind.Bool, "speechSettings.groundCrew.pushbackCrew.engineStartClearance", DependsOn = "Enabled")]
    public bool EngineStartClearance { get; set; } = true;

    // ---- Wording (GroundCrew role, verbatim) ----

    [OptionField(FieldKind.Text, "speechSettings.groundCrew.pushbackCrew.wording.bridgeRemoved", DependsOn = "Enabled")]
    public string BridgeRemovedText { get; set; } = "Cockpit, ground — bridge is off, all doors closed and checked.";

    [OptionField(FieldKind.Text, "speechSettings.groundCrew.pushbackCrew.wording.tugConnected", DependsOn = "Enabled")]
    public string TugConnectedText { get; set; } = "Cockpit, ground — tug connected, bypass pin inserted.";

    /// <summary>Appended to the tug line when <see cref="BrakesCheck"/> is on.</summary>
    [OptionField(FieldKind.Text, "speechSettings.groundCrew.pushbackCrew.wording.brakesRequest", DependsOn = "Enabled; BrakesCheck")]
    public string BrakesRequestText { get; set; } = "Release parking brake, please.";

    /// <summary>The answer to the captain's "brakes released".</summary>
    [OptionField(FieldKind.Text, "speechSettings.groundCrew.pushbackCrew.wording.clearedToPush", DependsOn = "Enabled; BrakesCheck")]
    public string ClearedToPushText { get; set; } = "Brakes released, copied. Cleared to push, {direction}.";

    /// <summary>Spoken as the push begins when no clearance was given by voice.</summary>
    [OptionField(FieldKind.Text, "speechSettings.groundCrew.pushbackCrew.wording.pushing", DependsOn = "Enabled")]
    public string PushingText { get; set; } = "Pushing back, {direction}.";

    [OptionField(FieldKind.Text, "speechSettings.groundCrew.pushbackCrew.wording.engineStart", DependsOn = "Enabled; EngineStartClearance")]
    public string EngineStartText { get; set; } = "Clear to start engines.";

    [OptionField(FieldKind.Text, "speechSettings.groundCrew.pushbackCrew.wording.pushComplete", DependsOn = "Enabled")]
    public string PushCompleteText { get; set; } = "Pushback complete — set parking brake.";

    [OptionField(FieldKind.Text, "speechSettings.groundCrew.pushbackCrew.wording.engineStartConfirmed", DependsOn = "Enabled")]
    public string EngineStartConfirmedText { get; set; } = "Good start confirmed — disconnecting, stand by for the hand signal.";

    [OptionField(FieldKind.Text, "speechSettings.groundCrew.pushbackCrew.wording.clear", DependsOn = "Enabled")]
    public string ClearText { get; set; } = "Cockpit, ground — bypass pin removed, you are clear. Hand signal on your left — have a good flight.";

    /// <summary>The answer to the captain's "ready for pushback".</summary>
    [OptionField(FieldKind.Text, "speechSettings.groundCrew.pushbackCrew.wording.readyAck", DependsOn = "Enabled")]
    public string ReadyAckText { get; set; } = "Copied — stand by for the tug.";
}
