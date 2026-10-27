namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// Settings for GSX Pro integration. The Couatl Remote API port is intentionally NOT configurable
/// here — it is always read from CouatlAddons.ini (see docs/integrations/gsx.md).
/// </summary>
public sealed class GsxOptions : IOptionSection
{
    public static string SectionName => "gsx";

    /// <summary>Master switch for the GSX ground automation pillar.</summary>
    [OptionField(FieldKind.Bool, "setup.features.gsx")]
    public bool Enabled { get; set; } = true;

    /// <summary>Delay between Remote API reconnect attempts (the first retry).</summary>
    [OptionField(FieldKind.Number, "gsxSettings.connection.reconnect", Suffix = "ms", Min = "500", Advanced = true)]
    public int ReconnectIntervalMs { get; set; } = 5000;

    /// <summary>Ceiling for the reconnect delay: each consecutive failure doubles the wait
    /// from <see cref="ReconnectIntervalMs"/> up to this (issue #76 — 27 attempts at a fixed
    /// 7 s before GSX had even started). Resets on the first successful connect.</summary>
    [OptionField(FieldKind.Number, "gsxSettings.connection.ceiling", Suffix = "ms", Min = "500", Advanced = true)]
    public int ReconnectMaxIntervalMs { get; set; } = 60000;

    /// <summary>How long to await a command result before synthesizing a timeout.</summary>
    [OptionField(FieldKind.Number, "gsxSettings.timeouts.command", Suffix = "ms", Min = "1000", Advanced = true)]
    public int CommandTimeoutMs { get; set; } = 10_000;

    /// <summary>How long to wait for a menu to appear (menuShown + matching title).</summary>
    [OptionField(FieldKind.Number, "gsxSettings.timeouts.menuOpen", Suffix = "ms", Min = "1000", Advanced = true)]
    public int MenuOpenTimeoutMs { get; set; } = 5000;

    /// <summary>Default budget for verifying a menu pick's observable effect (per-intent
    /// overridable — the reposition submenu is known to exceed 5 s).</summary>
    [OptionField(FieldKind.Number, "gsxSettings.timeouts.intentVerify", Suffix = "ms", Min = "1000", Advanced = true)]
    public int IntentVerifyTimeoutMs { get; set; } = 5000;

    // ---- Ground automation ----

    /// <summary>Master switch for the automation layer (question answering + service
    /// sequencing). The Remote API client/diagnostics run regardless.</summary>
    [OptionField(FieldKind.Bool, "gsxSettings.automation.enabled")]
    public bool AutomationEnabled { get; set; } = true;

    /// <summary>The cockpit "next service" smart button (predecessor semantics): a switch
    /// reaching its trigger value force-calls the next departure service while the sequence
    /// runs on the ground. Off for a panel or audio bridge that keys the interphone mic when
    /// INT/RAD is set to INT — the default trigger would key the mic on every press
    /// (2026-10-17).</summary>
    [OptionField(FieldKind.Bool, "gsxSettings.automation.smartButton")]
    public bool SmartButtonEnabled { get; set; } = true;

    /// <summary>ProSim switch dataref for the "next service" smart button. Empty = built-in
    /// INT/RAD rocker on both ACPs (<c>S_ASP_INTRAD</c> + <c>S_ASP2_INTRAD</c>). Any other
    /// switch dataref (<c>system.switches.S_ASP_CAB_SEND</c>) is read as a number; one that
    /// never reports a number disables the trigger with one warning.</summary>
    [OptionField(FieldKind.Text, "gsxSettings.automation.smartButtonDataRef", Placeholder = "system.switches.S_ASP_INTRAD", DependsOn = "SmartButtonEnabled", Advanced = true)]
    public string SmartButtonDataRef { get; set; } = "";

    /// <summary>Value that fires the trigger. Default 0 (INT on the INT/RAD rocker); momentary
    /// keys usually fire on 1.</summary>
    [OptionField(FieldKind.Number, "gsxSettings.automation.smartButtonValue", DependsOn = "SmartButtonEnabled", Advanced = true)]
    public int SmartButtonTriggerValue { get; set; }

    /// <summary>Start the departure service sequence automatically once preconditions hold
    /// (off by default — <c>StartDepartureServices</c> can always be invoked explicitly).</summary>
    [OptionField(FieldKind.Bool, "gsxSettings.automation.autoStart")]
    public bool AutoStartDepartureServices { get; set; }

    /// <summary>When the ground-preparation chain (reposition → gate anchor → GPU/chocks →
    /// jetway) may begin: <c>"auto"</c> runs it as soon as its preconditions hold (default);
    /// <c>"voice"</c> holds the whole chain until departure services are started — "commence
    /// ground services", the web Start button, or the API all release it (ADR-0006: voice is
    /// an additional trigger, never the only one); <c>"voiceThenAuto"</c> (issue #169) keeps
    /// the voice gate for the first leg of the session and starts a turnaround by itself once
    /// the next leg's OFP is loaded at the stand (after
    /// <see cref="TurnaroundAutoStartDelaySeconds"/>, or on the captain's yes with
    /// <see cref="TurnaroundAskFirst"/>). Unknown values read as auto.</summary>
    [OptionField(FieldKind.Select, "gsxSettings.automation.groundPrep", Choices = "groundPrepActivation")]
    public string GroundPrepActivation { get; set; } = "auto";

    /// <summary>The <see cref="GroundPrepActivation"/> value that holds for the crew's call.</summary>
    public const string GroundPrepActivationVoice = "voice";

    /// <summary>The <see cref="GroundPrepActivation"/> value that holds for the crew's call on
    /// a cold start and starts turnarounds itself (issue #169).</summary>
    public const string GroundPrepActivationVoiceThenAuto = "voiceThenAuto";

    /// <summary>True when the value holds ground preparation and the departure sequence for
    /// the crew's "commence ground services" (both voice modes). Unknown values read as auto —
    /// a typo in settings.json must not silently park the whole prep chain.</summary>
    public static bool IsVoiceGated(string? groundPrepActivation)
        => string.Equals(groundPrepActivation, GroundPrepActivationVoice, StringComparison.OrdinalIgnoreCase)
            || IsVoiceThenAuto(groundPrepActivation);

    /// <summary>True when a turnaround starts itself (issue #169).</summary>
    public static bool IsVoiceThenAuto(string? groundPrepActivation)
        => string.Equals(groundPrepActivation, GroundPrepActivationVoiceThenAuto, StringComparison.OrdinalIgnoreCase);

    /// <summary>With <see cref="GroundPrepActivation"/> = voiceThenAuto: how long the
    /// turnaround conditions (at the stand, engines off, next leg's OFP loaded) must stand
    /// before the departure sequence starts itself — the import needs to settle and the crew
    /// a moment to object. 2026-10-11 evidence (issue #169): the second OFP landed 21 s
    /// before the Preflight edge.</summary>
    [OptionField(FieldKind.Number, "gsxSettings.automation.turnaroundDelay", Suffix = "s", Min = "0", DependsOn = "GroundPrepActivation=voiceThenAuto", NoDefault = true)]
    public int TurnaroundAutoStartDelaySeconds { get; set; } = 30;

    /// <summary>With voiceThenAuto: instead of starting the turnaround by itself the FO asks
    /// once — "Next plan loaded — commence ground services?" — and starts on yes. No answer
    /// leaves the voice gate as it was ("commence ground services" / the Start button).</summary>
    [OptionField(FieldKind.Bool, "gsxSettings.automation.turnaroundAsk", DependsOn = "GroundPrepActivation=voiceThenAuto")]
    public bool TurnaroundAskFirst { get; set; }

    /// <summary>The out-of-the-box departure queue (fresh instances per call — the steps are
    /// mutable). Legacy Prosim2GSX defaults: Cleaning + Lavatory on turnarounds only, Refuel +
    /// Catering side by side, Water once catering is requested, board last. Used both as the
    /// property default (for the settings-defaults writer) and to restore the list when the
    /// settings file omits it.</summary>
    public static IReadOnlyList<DepartureServiceStep> DefaultDepartureServices =>
    [
        new("Cleaning", GsxServiceActivation.AfterCalled, GsxServiceConstraint.TurnAround),
        new("Lavatory", GsxServiceActivation.AfterCalled, GsxServiceConstraint.TurnAround),
        new("Refueling", GsxServiceActivation.AfterCalled),
        new("Catering", GsxServiceActivation.AfterCalled),
        new("Water", GsxServiceActivation.AfterRequested),
        new("Boarding", GsxServiceActivation.AfterAllCompleted),
    ];

    /// <summary>The ordered departure-service queue: list position is call order; each entry's
    /// activation gates it on the previous entry's observed GSX state (Prosim2GSX model).
    /// Remove an entry (or set activation Skip) to disable a service.</summary>
    public List<DepartureServiceStep> DepartureServices { get; set; } = [.. DefaultDepartureServices];

    /// <summary>How long a sent service.trigger may wait for GSX to visibly pick the service up
    /// (mirror shows requested/active) before the call is considered dropped and retried. The
    /// trigger ack alone proves nothing — GSX drops rapid-fire requests silently.</summary>
    [OptionField(FieldKind.Number, "gsxSettings.timeouts.triggerConfirm", Suffix = "ms", Min = "1000", Advanced = true)]
    public int TriggerConfirmTimeoutMs { get; set; } = 10_000;

    /// <summary>Hold Refueling/Boarding until the SimBrief OFP is imported into ProSim.</summary>
    [OptionField(FieldKind.Bool, "gsxSettings.automation.requireOfp")]
    public bool RequireOfpBeforeDeparture { get; set; } = true;

    // ---- Aircraft state check (issue #63) ----

    /// <summary>Verify the aircraft against the expected cold-and-dark switch set once per
    /// session, on a fresh departure (skipped on turnarounds and mid-flight restarts). The
    /// expected positions live in <c>config\aircraft-states\cold-and-dark.json</c> in the
    /// user config tree.</summary>
    [OptionField(FieldKind.Bool, "gsxSettings.coldDark.verify")]
    public bool AircraftStateCheckEnabled { get; set; } = true;

    /// <summary>The FO speaks one advisory listing the first few discrepancies when the check
    /// finds mismatches (the web Flight Status page shows the full list regardless).</summary>
    [OptionField(FieldKind.Bool, "gsxSettings.coldDark.announce", DependsOn = "AircraftStateCheckEnabled")]
    public bool AircraftStateCheckAnnounce { get; set; } = true;

    // ---- Voice control ----

    /// <summary>Voice phrases for GSX ground services ("request boarding", "cockpit to
    /// ground", …) routed through the same named commands the web UI and Stream Deck use.
    /// On by default; the speech pillar itself must also be running.</summary>
    public bool VoiceControlEnabled { get; set; } = true;

    // ---- Question answering ----

    [OptionField(FieldKind.Bool, "gsxSettings.questions.followMe")]
    public bool SkipFollowMe { get; set; } = true;

    /// <summary>Answer "board crew"/"deboard crew" questions automatically.</summary>
    [OptionField(FieldKind.Bool, "gsxSettings.questions.crew")]
    public bool AnswerCrewQuestions { get; set; } = true;

    /// <summary>GSX 4's crew menu offers "Nobody" | "Crew" | "Pilots" | "Both" (smoke-test
    /// verified — it is no longer yes/no). This exact entry is picked.</summary>
    [OptionField(FieldKind.Select, "gsxSettings.questions.crewAnswer", Choices = "crewAnswer", DependsOn = "AnswerCrewQuestions")]
    public string CrewBoardingAnswer { get; set; } = "Both";

    /// <summary>Write the CREW/PILOTS_NOT_(DE)BOARDING LVARs so GSX never asks the crew
    /// question at all (the predecessor's SkipCrewQuestion). Off by default — the menu answer
    /// above already handles it; turn this on to skip crew boarding entirely.</summary>
    [OptionField(FieldKind.Bool, "gsxSettings.questions.suppressCrew")]
    public bool SkipCrewBoardingQuestion { get; set; }

    /// <summary>Answer the "Do you want to request …" pushback confirmation with yes.</summary>
    [OptionField(FieldKind.Bool, "gsxSettings.questions.confirmPushback")]
    public bool ConfirmPushbackRequest { get; set; } = true;

    /// <summary>Answer to GSX's "Attach Pushback Tug" question during boarding: "ignore"
    /// leaves the menu for the user; "no" | "yes" pick positionally (predecessor rule:
    /// yes = entry 1, no = entry 2 — the menu is not literal yes/no text).</summary>
    [OptionField(FieldKind.Select, "gsxSettings.questions.tug", Choices = "tugAnswer")]
    public string TugQuestionAnswer { get; set; } = "no";

    /// <summary>When the tug attached during boarding (answered "yes" above or attached by
    /// hand), auto-call Pushback: "never" | "afterDepartureServices" | "afterFinalLoadsheet"
    /// (predecessor default).</summary>
    [OptionField(FieldKind.Select, "gsxSettings.questions.tugPushback", Choices = "tugPushback")]
    public string CallPushbackWhenTugAttached { get; set; } = "afterFinalLoadsheet";

    /// <summary>
    /// What answers GSX's "Select pushback direction" menu when the pilot has not chosen a
    /// direction for THIS flight (voice "tail left" / "push back facing north", the OFP page's
    /// Korry buttons, the API — those always win):
    /// <c>"auto"</c> (default since 2026-10-04) applies the advisor's suggestion when it is
    /// confident — the push that faces the departure runway, from the GSX profile's routes,
    /// the stand heading and the runway position — and otherwise asks;
    /// <c>"ask"</c> never guesses: the FO asks "Tail left or tail right?" and the menu waits;
    /// <c>"straight"</c> | <c>"tailLeft"</c> | <c>"tailRight"</c> are the old fixed answers
    /// (Prosim2GSX's tri-state); when the stand does not offer the fixed answer the FO takes
    /// the only direction on the menu or asks (issue #161) — the menu is never left silently.
    /// Unknown values read as auto.
    /// </summary>
    [OptionField(FieldKind.Select, "gsxSettings.pushbackDirection.default", Choices = "pushbackMode")]
    public string PushbackPreference { get; set; } = "auto";

    /// <summary>With <see cref="PushbackPreference"/> = auto and no confident suggestion, ask
    /// the pilot by voice (on) or leave GSX's menu open silently (off).</summary>
    [OptionField(FieldKind.Bool, "gsxSettings.pushbackDirection.askWhenUnsure", DependsOn = "PushbackPreference=auto")]
    public bool PushbackAskWhenUnsure { get; set; } = true;

    /// <summary>Accept the de-icing offer and select fluid automatically.</summary>
    [OptionField(FieldKind.Bool, "gsxSettings.deice.autoAccept")]
    public bool AutoDeIce { get; set; }

    /// <summary>"Type I" | "Type II" | "Type IV" — matched as a token in the fluid menu entry.</summary>
    [OptionField(FieldKind.Select, "gsxSettings.deice.fluid", Choices = "deIceFluid", DependsOn = "AutoDeIce | Deice.AutoRequest=ask | Deice.AutoRequest=auto")]
    public string DeIceFluidType { get; set; } = "Type IV";

    /// <summary>"75" | "100" — concentration token matched in the fluid menu entry.</summary>
    [OptionField(FieldKind.Select, "gsxSettings.deice.concentration", Choices = "deIceConcentration", DependsOn = "AutoDeIce | Deice.AutoRequest=ask | Deice.AutoRequest=auto")]
    public string DeIceConcentration { get; set; } = "75";

    /// <summary>The de-icing auto-request policy (2026-10-09): whether the app itself puts
    /// de-icing on the departure sequence from the outside air temperature and the departure
    /// METAR. The fluid and concentration answers above are reused — they are the same
    /// crew preference whether GSX or the app raised the request.</summary>
    public GsxDeiceOptions Deice { get; set; } = new();

    /// <summary>
    /// Click GSX's own "SimBrief" gate-menu line once per new OFP (2026-10-09, the
    /// Prosim2GSX <c>ReloadSimbrief</c> port): GSX re-reads the SimBrief flight plan so its
    /// VDGS / marshaller boards show the right flight number, destination and ETD. Only at
    /// the gate with GSX Ready — never in flight — and off by default (owner's conservative
    /// default: the pick is a text match on a menu line GSX has never shown this app live).
    /// The Status section button and <c>gsx.reloadSimbrief</c> stay available either way.
    /// </summary>
    [OptionField(FieldKind.Bool, "gsxSettings.simbrief.reload")]
    public bool ReloadSimbriefOnNewOfp { get; set; }

    /// <summary>How many "Next Page" turns of GSX's "Additional Services" menu the SimBrief
    /// reload walks looking for the "Reload Simbrief" row (issue #174, 2026-10-11: the row is
    /// on neither the gate menu nor page 1 of Additional Services on the recorded flights;
    /// page 2 had never been opened). Bounded so a GSX without the row never loops.</summary>
    [OptionField(FieldKind.Number, "gsxSettings.simbrief.pages", Min = "0")]
    public int SimbriefReloadMenuPages { get; set; } = 4;

    /// <summary>Ordered operator keywords for handling/catering operator menus; the
    /// "[GSX choice]" token is always an accepted fallback. Empty list ⇒ menus are left for
    /// the user unless <see cref="AutoSelectOperator"/> suppresses them.</summary>
    public List<string> OperatorPreferences { get; set; } = [];

    /// <summary>Write handler.set autoSelectOperator once per gate session so the operator
    /// popup never surfaces (falls back to the operator menu handler when unsupported).</summary>
    [OptionField(FieldKind.Bool, "gsxSettings.operators.auto")]
    public bool AutoSelectOperator { get; set; } = true;

    /// <summary>Arrival gate to arm automatically when reaching flight (e.g. "B12"); empty ⇒
    /// none (a gate can still be armed from the GSX diagnostics page).</summary>
    [OptionField(FieldKind.Text, "gsxSettings.arrival.gate", Normalize = FieldNormalization.Trim)]
    public string ArrivalGate { get; set; } = "";

    /// <summary>ICAO prefixes (1–4 letters) identifying company-hub airports, matched against
    /// the loaded airport for the CompanyHub/NonCompanyHub departure-service constraints.</summary>
    public List<string> CompanyHubs { get; set; } = [];

    // ---- ProSim sync modules ----

    /// <summary>When the departure sequence orders the fuel truck (2026-09-19, real-world
    /// SOP request): <c>"automatic"</c> calls Refueling in departure order on the current
    /// figure (legacy); <c>"onFuelConfirmed"</c> holds ONLY the Refueling step until the crew
    /// confirms the block fuel (INIT page CONFIRM FUEL, "fuel confirmed" by voice, a direct
    /// refuel request, or the command API) — the other services continue. Unknown values
    /// read as automatic.</summary>
    [OptionField(FieldKind.Select, "gsxSettings.refuel.call", Choices = "refuelCall")]
    public string RefuelCall { get; set; } = "automatic";

    /// <summary>The <see cref="RefuelCall"/> value that holds Refueling for the crew's
    /// confirmation.</summary>
    public const string RefuelCallOnFuelConfirmed = "onFuelConfirmed";

    /// <summary>True when a <see cref="RefuelCall"/> value asks for the crew's fuel
    /// confirmation (a method, not a property, so the settings writer never serializes it).</summary>
    public static bool IsRefuelOnFuelConfirmed(string? refuelCall)
        => string.Equals(refuelCall, RefuelCallOnFuelConfirmed, StringComparison.OrdinalIgnoreCase);

    /// <summary>Step ProSim fuel toward the target while the GSX hose is connected.</summary>
    [OptionField(FieldKind.Bool, "gsxSettings.refuel.sync")]
    public bool RefuelSyncEnabled { get; set; } = true;

    /// <summary>Fuel transfer rate in kg per second.</summary>
    [OptionField(FieldKind.Number, "gsxSettings.refuel.rate", Suffix = "kg/s", Step = "0.5", Min = "1", DependsOn = "RefuelSyncEnabled")]
    public double RefuelRateKgPerSec { get; set; } = 25;

    /// <summary>Rate model: "fixedRate" pumps at <see cref="RefuelRateKgPerSec"/>;
    /// "dynamicRate" computes the rate once per transfer so the fill takes about
    /// <see cref="RefuelTimeTargetSeconds"/> regardless of the ordered amount.</summary>
    [OptionField(FieldKind.Select, "gsxSettings.refuel.method", Choices = "refuelMethod", DependsOn = "RefuelSyncEnabled")]
    public string RefuelMethod { get; set; } = "fixedRate";

    /// <summary>Target fill duration in seconds for the dynamic rate method.</summary>
    [OptionField(FieldKind.Number, "gsxSettings.refuel.timeTarget", Suffix = "s", Min = "30", DependsOn = "RefuelSyncEnabled")]
    public int RefuelTimeTargetSeconds { get; set; } = 300;

    /// <summary>Tankering: when the FOB already meets the planned figure (OFP block fuel, else
    /// the EFB planned fuel; 25 kg tolerance) the departure sequence retires Refueling without
    /// calling GSX at all (#117) and raises the preliminary-loadsheet trigger in its place. A
    /// refuel called anyway (GSX menu, plan arriving mid-service) still moves no fuel.
    /// Predecessor default on.</summary>
    [OptionField(FieldKind.Bool, "gsxSettings.refuel.skipTankering")]
    public bool SkipRefuelOnTankering { get; set; } = true;

    /// <summary>Snap the FOB to the target when the GSX fuel hose disconnects mid-transfer,
    /// instead of pausing and waiting for it to reconnect.</summary>
    [OptionField(FieldKind.Bool, "gsxSettings.refuel.finishOnHose", DependsOn = "RefuelSyncEnabled")]
    public bool RefuelFinishOnHose { get; set; }

    /// <summary>Allow the sync to pump fuel DOWN when the target is below the current quantity.
    /// Off by default: ProSim's fuel-target datarefs have twice exposed transfer amounts instead
    /// of totals, and an erroneous low target must hold (decision-logged), never defuel.</summary>
    [OptionField(FieldKind.Bool, "gsxSettings.refuel.allowDefuel", DependsOn = "RefuelSyncEnabled")]
    public bool AllowDefuel { get; set; }

    /// <summary>Mirror GSX boarding counters into ProSim pax zones and cargo holds.</summary>
    [OptionField(FieldKind.Bool, "gsxSettings.boarding.sync")]
    public bool BoardingSyncEnabled { get; set; } = true;

    /// <summary>Place GPU + chocks (+ PCA per <see cref="PcaMode"/>) at session start on the
    /// ground, and remove them when the beacon comes on.</summary>
    [OptionField(FieldKind.Bool, "gsxSettings.equipment.place")]
    public bool AutoGroundEquipment { get; set; } = true;

    /// <summary>Also place/remove preconditioned air with the ground equipment (legacy bool —
    /// superseded by <see cref="PcaMode"/> and only consulted when that is empty).</summary>
    public bool AutoPca { get; set; }

    /// <summary>PCA tri-state (predecessor ConnectPca): "never" | "always" | "onlyJetway"
    /// (place only at jetway stands, detected from GSX's jetway availability). Empty falls
    /// back to the legacy <see cref="AutoPca"/> bool.</summary>
    [OptionField(FieldKind.Select, "gsxSettings.equipment.pca", Choices = "pcaMode")]
    public string PcaMode { get; set; } = "";

    /// <summary>Disconnect PCA at session start when it is connected (e.g. from a saved panel
    /// state) but not allowed by <see cref="PcaMode"/> (predecessor PcaOverride).</summary>
    [OptionField(FieldKind.Bool, "gsxSettings.equipment.pcaOverride", Advanced = true)]
    public bool PcaOverride { get; set; } = true;

    /// <summary>Connect the GPU at session start even when the APU is already running; off
    /// skips the GPU on APU power (predecessor ConnectGpuWithApuRunning).</summary>
    [OptionField(FieldKind.Bool, "gsxSettings.equipment.gpuWithApu", DependsOn = "AutoGroundEquipment")]
    public bool ConnectGpuWithApuRunning { get; set; } = true;

    /// <summary>Condition-driven equipment removal during the pushback phase (predecessor
    /// GradualGroundEquipRemoval): the GPU clears once external power has fed the buses and
    /// then goes off (a GPU never switched on is left alone — 2026-09-25 cold-and-dark fix),
    /// the chocks once the park brake is set and the GPU is gone. Only meaningful with the
    /// beacon-orchestrated sequence OFF (the sequence owns its own timed removal).</summary>
    [OptionField(FieldKind.Bool, "gsxSettings.equipment.gradualRemoval")]
    public bool GradualGroundEquipRemoval { get; set; }

    /// <summary>Randomized delay before chocks are placed after arriving stably parked
    /// (predecessor ChockDelayMin/Max), seconds.</summary>
    [OptionField(FieldKind.Range, "gsxSettings.equipment.chockDelay", RangeEnd = nameof(ChockDelayMaxSec), Suffix = "s", Min = "0", DependsOn = "AutoGroundEquipment")]
    public int ChockDelayMinSec { get; set; } = 10;

    [OptionField(FieldKind.RangeUpper, RangeStart = nameof(ChockDelayMinSec))]
    public int ChockDelayMaxSec { get; set; } = 20;

    /// <summary>Operate the jetway (or call stairs at jetway-less gates) automatically once per
    /// gate session at SESSION START, after checking they are not already connected
    /// (predecessor CallJetwayStairsOnPrep).</summary>
    [OptionField(FieldKind.Bool, "gsxSettings.jetway.connectAtStart")]
    public bool AutoConnectJetwayOrStairs { get; set; } = true;

    /// <summary>Connect the jetway/stairs when the departure services start, for sessions
    /// where the session-start connect is off (predecessor CallJetwayStairsDuringDeparture).</summary>
    [OptionField(FieldKind.Bool, "gsxSettings.jetway.connectOnDeparture")]
    public bool CallJetwayStairsDuringDeparture { get; set; }

    /// <summary>Remove the stairs once every departure service completed: "never" | "always" |
    /// "onlyJetway" (only when a jetway also serves the pax doors — predecessor default).</summary>
    [OptionField(FieldKind.Select, "gsxSettings.jetway.removeStairs", Choices = "removeStairs")]
    public string RemoveStairsAfterDeparture { get; set; } = "onlyJetway";

    /// <summary>Retract jetway AND stairs when the final loadsheet is transmitted (predecessor
    /// RemoveJetwayStairsOnFinal; ignored while the beacon sequence owns removal timing).</summary>
    [OptionField(FieldKind.Bool, "gsxSettings.jetway.removeOnFinal")]
    public bool RemoveJetwayStairsOnFinal { get; set; } = true;

    /// <summary>Connect the jetway/stairs on arrival once stably parked (predecessor
    /// CallJetwayStairsOnArrival).</summary>
    [OptionField(FieldKind.Bool, "gsxSettings.jetway.connectOnArrival")]
    public bool CallJetwayStairsOnArrival { get; set; } = true;

    /// <summary>Run GSX's "Reposition Aircraft" once at session start on the ground.</summary>
    [OptionField(FieldKind.Bool, "gsxSettings.equipment.reposition")]
    public bool AutoReposition { get; set; } = true;

    /// <summary>
    /// Re-anchor GSX's remembered parking to the stand the aircraft actually occupies (a
    /// gate.select for the current gate) during ground preparation. GSX persists its assigned
    /// facility across sim sessions; starting a new flight at a different stand otherwise
    /// leaves it split between two gates and every service trigger is silently dropped
    /// (issue #44: previous session ended at D5, new flight spawned at D27).
    /// </summary>
    public bool AnchorDepartureGate { get; set; } = true;

    /// <summary>
    /// Answer GSX's own "Select Position at &lt;destination&gt;" menu with the arrival gate
    /// (issue #156). GSX raises that menu itself on the landing roll when no gate was
    /// pre-selected; with this on, the app picks the facility row whose range covers the
    /// requested gate and then the position row that names it — text-matched only, any miss
    /// leaves the menu for you. Off by default until the menu shape is verified live.
    /// </summary>
    [OptionField(FieldKind.Bool, "gsxSettings.arrival.answerPositionMenu")]
    public bool AnswerPositionMenuWithArrivalGate { get; set; }

    /// <summary>How many "Next Page" turns of GSX's in-flight "Select airport" list the
    /// airport pick walks looking for the destination row (issue #75, 2026-10-11: page 1
    /// lists the PLANNED airport and the nearest fields only; EFHK was never on it).</summary>
    [OptionField(FieldKind.Number, "gsxSettings.arrival.airportPages", Min = "1")]
    public int AirportPickMenuPages { get; set; } = 4;

    /// <summary>When GSX answers the arrival gate with suffix variants (W36 → 36A / 36B) and
    /// neither the pilot's text nor a lone jetway decides: ask the pilot once (web buttons +
    /// the FO's question) and take the first variant at taxi-in without an answer. Off: take
    /// the first variant at once.</summary>
    [OptionField(FieldKind.Bool, "gsxSettings.arrival.askStand")]
    public bool AskPilotOnAmbiguousGate { get; set; } = true;

    // ---- Airport parking knowledge (GSX profiles + scenery) ----

    /// <summary>
    /// Read the airport's GSX profile (<c>.ini</c> stand data + <c>.py</c> name templates) and
    /// the scenery's parking list before sending a gate, so "W40" becomes the stand GSX knows as
    /// "Gate 40" (EFHK, 2026-10-04: the template drops the GATE letter, and the bare token was
    /// refused every time). Also feeds the pushback-direction suggestion. On by default.
    /// </summary>
    [OptionField(FieldKind.Bool, "gsxSettings.standKnowledge.resolve")]
    public bool ResolveGatesFromProfiles { get; set; } = true;

    /// <summary>GSX's user profile folder; empty ⇒ <c>%APPDATA%\Virtuali\GSX\MSFS</c> on this
    /// machine (set it when the app runs on a different PC from the simulator and the folder is
    /// shared).</summary>
    [OptionField(FieldKind.Text, "gsxSettings.standKnowledge.profileFolder", TextKind = TextFieldKind.FolderPath, BrowseStartIn = "Gsx", DependsOn = "ResolveGatesFromProfiles", Normalize = FieldNormalization.Path)]
    public string GsxProfileFolder { get; set; } = "";

    /// <summary>MSFS package roots (the folders holding <c>Community</c> and <c>Official</c>),
    /// searched for profiles shipped inside scenery packages. Empty ⇒ auto-detected from the
    /// Store/Steam install locations and <c>UserCfg.opt</c> on this machine.</summary>
    public List<string> SceneryPackageFolders { get; set; } = [];

    /// <summary>Turn off ProSim's own GSX auto-integration flags (efb.gsx.*) while this
    /// application drives GSX — prevents the two automations fighting each other.</summary>
    [OptionField(FieldKind.Bool, "gsxSettings.equipment.disableProsimNative", Advanced = true)]
    public bool DisableProsimNativeGsx { get; set; } = true;

    // ---- Door automation ----

    /// <summary>Drive ProSim aircraft doors from GSX activity: cargo doors open for
    /// boarding/deboarding and close when loading finishes, catering (service) door toggles
    /// follow GSX's requests, and entry doors open when stairs dock at jetway-less stands.</summary>
    [OptionField(FieldKind.Bool, "gsxSettings.doors.automation")]
    public bool DoorAutomationEnabled { get; set; } = true;

    /// <summary>Write L:FSDT_GSX_DISABLE_DOORS_MSG so GSX never shows "waiting for your action"
    /// door prompts while this application drives the doors. Re-asserted after Couatl restarts
    /// (the LVAR resets to 0).</summary>
    [OptionField(FieldKind.Bool, "gsxSettings.doors.suppressMessages", Advanced = true)]
    public bool SuppressGsxDoorMessages { get; set; } = true;

    /// <summary>Delay before the cargo doors open once boarding/deboarding begins.</summary>
    [OptionField(FieldKind.Number, "gsxSettings.doors.cargoOpenDelay", Suffix = "s", Min = "0", DependsOn = "DoorAutomationEnabled; DoorOpenOnBoardingActive")]
    public int CargoDoorOpenDelaySec { get; set; } = 2;

    /// <summary>Delay before a cargo door closes after its GSX loader finishes.</summary>
    [OptionField(FieldKind.Number, "gsxSettings.doors.cargoCloseDelay", Suffix = "s", Min = "0", DependsOn = "DoorAutomationEnabled; DoorCargoHandling")]
    public int CargoDoorCloseDelaySec { get; set; } = 16;

    /// <summary>Leave the cargo doors open after deboarding completes (ground crew realism
    /// option; default closes them).</summary>
    [OptionField(FieldKind.Bool, "gsxSettings.doors.keepOpenAfterUnload", DependsOn = "DoorAutomationEnabled")]
    public bool KeepCargoDoorsOpenAfterUnload { get; set; }

    /// <summary>Pax doors follow GSX stairs docking/leaving (predecessor DoorStairHandling —
    /// L4 always, L1 only at jetway-less stands).</summary>
    [OptionField(FieldKind.Bool, "gsxSettings.doors.stairs", DependsOn = "DoorAutomationEnabled")]
    public bool DoorStairHandling { get; set; } = true;

    /// <summary>Starboard service doors follow GSX catering requests (predecessor
    /// DoorCateringHandling — the SERVICE_n toggles).</summary>
    [OptionField(FieldKind.Bool, "gsxSettings.doors.catering", DependsOn = "DoorAutomationEnabled")]
    public bool DoorCateringHandling { get; set; } = true;

    /// <summary>Catering door choice: learn it from GSX.</summary>
    public const string CateringDoorAuto = "auto";

    /// <summary>Catering door choice: the forward service door (1R).</summary>
    public const string CateringDoorForward = "forward";

    /// <summary>Catering door choice: the aft service door (4R).</summary>
    public const string CateringDoorAft = "aft";

    /// <summary>Which starboard service door the catering truck serves: <c>auto</c> (the
    /// default — learned from GSX, see <see cref="CateringDoorLearned"/>), <c>forward</c> (1R,
    /// the usual A320 setup) or <c>aft</c> (4R). The door is set in the user's GSX aircraft
    /// profile, which the app cannot read; GSX reveals it by pulsing
    /// <c>AIRCRAFT_SERVICE_1_TOGGLE</c> (1R) or <c>AIRCRAFT_SERVICE_2_TOGGLE</c> (4R) when the
    /// truck wants the door. The diagnostics GSX page places the CATERING box beside the
    /// resolved door and draws its connector there (owner request 2026-10-27; auto-learning
    /// added the same day after a flight where the truck came to 4R while the page showed 1R).</summary>
    [OptionField(FieldKind.Select, "gsxSettings.doors.cateringDoor", Choices = "cateringDoor")]
    public string CateringDoor { get; set; } = CateringDoorAuto;

    /// <summary>The service door GSX last asked for (<c>forward</c> or <c>aft</c>), written by
    /// the door service when a service-door toggle pulses; null until GSX has asked once. Read
    /// only while <see cref="CateringDoor"/> is <c>auto</c>. Not edited on a page — the Catering
    /// door hint shows it.</summary>
    public string? CateringDoorLearned { get; set; }

    /// <summary>The door the catering truck is taken to serve, <c>forward</c> or <c>aft</c>:
    /// the user's explicit pick, else the learned door, else forward (the usual A320 profile).
    /// A static method, not a property, so the draft serializer never writes it to the file.</summary>
    public static string EffectiveCateringDoor(GsxOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.Equals(options.CateringDoor, CateringDoorAft, StringComparison.OrdinalIgnoreCase))
        {
            return CateringDoorAft;
        }

        if (string.Equals(options.CateringDoor, CateringDoorForward, StringComparison.OrdinalIgnoreCase))
        {
            return CateringDoorForward;
        }

        return string.Equals(options.CateringDoorLearned, CateringDoorAft, StringComparison.OrdinalIgnoreCase)
            ? CateringDoorAft
            : CateringDoorForward;
    }

    /// <summary>Cargo doors follow GSX boarding/deboarding (predecessor DoorCargoHandling —
    /// the CARGO_n toggles plus loader-finished closes).</summary>
    [OptionField(FieldKind.Bool, "gsxSettings.doors.cargo", DependsOn = "DoorAutomationEnabled")]
    public bool DoorCargoHandling { get; set; } = true;

    /// <summary>Open both cargo doors when boarding/deboarding becomes active (predecessor
    /// DoorOpenBoardActive; the open delay is <see cref="CargoDoorOpenDelaySec"/>).</summary>
    [OptionField(FieldKind.Bool, "gsxSettings.doors.cargoOnBoarding", DependsOn = "DoorAutomationEnabled")]
    public bool DoorOpenOnBoardingActive { get; set; } = true;

    /// <summary>Skip the loader-finished cargo-door close during BOARDING (predecessor
    /// DoorsCargoKeepOpenOnLoaded) — the doors still close when boarding completes.</summary>
    [OptionField(FieldKind.Bool, "gsxSettings.doors.keepOpenAfterLoad", DependsOn = "DoorAutomationEnabled")]
    public bool KeepCargoDoorsOpenAfterLoad { get; set; }

    /// <summary>Close every open door once the final loadsheet is transmitted and boarding has
    /// completed (predecessor CloseDoorsOnFinal; ignored while the beacon sequence owns door
    /// timing).</summary>
    [OptionField(FieldKind.Bool, "gsxSettings.doors.closeOnFinal", DependsOn = "DoorAutomationEnabled")]
    public bool CloseDoorsOnFinal { get; set; } = true;

    // ---- Beacon-orchestrated pushback sequence ----

    /// <summary>Beacon on (with departure services complete) starts the orchestrated pushback
    /// prep: wait for APU → close doors → retract jetway/stairs → clear ground equipment →
    /// pushback, each step after a randomized crew-realism delay. Beacon off pauses.</summary>
    [OptionField(FieldKind.Bool, "gsxSettings.pushback.beacon")]
    public bool BeaconPushbackSequenceEnabled { get; set; } = true;

    /// <summary>Call the GSX Pushback service automatically when the sequence reaches
    /// ready-for-push (off = the sequence prepares everything and leaves the call to you).</summary>
    [OptionField(FieldKind.Bool, "gsxSettings.pushback.callAtReady", DependsOn = "BeaconPushbackSequenceEnabled")]
    public bool CallPushbackOnBeacon { get; set; } = true;

    /// <summary>Answer GSX's "Confirm good engine start" entry on the Interrupt-pushback menu
    /// automatically once an engine runs with the park brake set (issue #106). Off leaves that
    /// menu to the pilot — the tug waits until you answer it yourself. Had no switch until the
    /// 2026-10-08 settings audit (owner rule: every option has a web control).</summary>
    [OptionField(FieldKind.Bool, "gsxSettings.pushback.confirmEngineStart")]
    public bool ConfirmEngineStart { get; set; } = true;

    /// <summary>Randomized delay bounds (seconds) before the doors close.</summary>
    [OptionField(FieldKind.Range, "gsxSettings.beaconDelays.doorsClose", RangeEnd = nameof(SeqDoorsCloseDelayMaxSec), Suffix = "s", Min = "0", DependsOn = "BeaconPushbackSequenceEnabled")]
    public int SeqDoorsCloseDelayMinSec { get; set; } = 10;
    [OptionField(FieldKind.RangeUpper, RangeStart = nameof(SeqDoorsCloseDelayMinSec))]
    public int SeqDoorsCloseDelayMaxSec { get; set; } = 20;

    /// <summary>Randomized delay bounds (seconds) before the jetway/stairs retract.</summary>
    [OptionField(FieldKind.Range, "gsxSettings.beaconDelays.jetwayRetract", RangeEnd = nameof(SeqJetwayRetractDelayMaxSec), Suffix = "s", Min = "0", DependsOn = "BeaconPushbackSequenceEnabled")]
    public int SeqJetwayRetractDelayMinSec { get; set; } = 10;
    [OptionField(FieldKind.RangeUpper, RangeStart = nameof(SeqJetwayRetractDelayMinSec))]
    public int SeqJetwayRetractDelayMaxSec { get; set; } = 25;

    /// <summary>Randomized delay bounds (seconds) before ground equipment is cleared.</summary>
    [OptionField(FieldKind.Range, "gsxSettings.beaconDelays.equipmentClear", RangeEnd = nameof(SeqGpuDisconnectDelayMaxSec), Suffix = "s", Min = "0", DependsOn = "BeaconPushbackSequenceEnabled")]
    public int SeqGpuDisconnectDelayMinSec { get; set; } = 10;
    [OptionField(FieldKind.RangeUpper, RangeStart = nameof(SeqGpuDisconnectDelayMinSec))]
    public int SeqGpuDisconnectDelayMaxSec { get; set; } = 20;

    // ---- Arrival / deboarding ----

    /// <summary>Mirror GSX deboarding counters into ProSim: seats empty front-first as pax
    /// leave, cargo drains by GSX's unload percentage.</summary>
    [OptionField(FieldKind.Bool, "gsxSettings.arrival.deboardingSync")]
    public bool DeboardingSyncEnabled { get; set; } = true;

    /// <summary>Call the GSX Deboarding service automatically once stably parked on arrival.</summary>
    [OptionField(FieldKind.Bool, "gsxSettings.arrival.callDeboard")]
    public bool AutoCallDeboardOnArrival { get; set; } = true;

    /// <summary>How long the stable-parked condition (on ground, engines off, park brake set,
    /// beacon off, stationary) must hold before arrival actions fire.</summary>
    [OptionField(FieldKind.Number, "gsxSettings.arrival.stableHold", Suffix = "s", Min = "1")]
    public int ArrivalStableSeconds { get; set; } = 10;

    // ---- Fuel-on-board persistence ----

    /// <summary>Restore the saved FOB for this aircraft at preparation (before a plan is
    /// loaded) and save the FOB when stably parked on arrival — so the next session starts
    /// with the fuel you landed with.</summary>
    [OptionField(FieldKind.Bool, "gsxSettings.fuelMemory.remember")]
    public bool FuelSaveLoadFob { get; set; } = true;

    /// <summary>FOB written at preparation when no value has been saved for this aircraft yet.</summary>
    [OptionField(FieldKind.Number, "gsxSettings.fuelMemory.defaultFob", Suffix = "kg", Min = "0", DependsOn = "FuelSaveLoadFob")]
    public double FuelResetDefaultKg { get; set; } = 3000;

    /// <summary>Saved FOB per aircraft title — maintained by the app at arrival; not intended
    /// for hand editing (but harmless to edit).</summary>
    public Dictionary<string, double> FuelFobSaved { get; set; } = [];

    // ---- Passenger randomization ----

    /// <summary>Randomize the booked pax against the OFP: each seat flips with
    /// <see cref="NoShowChancePerSeat"/> (booked→empty = no-show, empty→booked = walk-up
    /// extra), with cargo adjusted by <see cref="WeightPerBagKg"/> per passenger delta.
    /// Off by default — the load then matches the OFP exactly.</summary>
    [OptionField(FieldKind.Bool, "gsxSettings.passengers.randomize")]
    public bool RandomizePaxNoShows { get; set; }

    /// <summary>Per-seat flip probability when <see cref="RandomizePaxNoShows"/> is on.</summary>
    [OptionField(FieldKind.Number, "gsxSettings.passengers.chance", Step = "0.001", Min = "0", Max = "1", DependsOn = "RandomizePaxNoShows")]
    public double NoShowChancePerSeat { get; set; } = 0.03;

    /// <summary>Checked-bag weight used to adjust cargo for no-shows/extras.</summary>
    [OptionField(FieldKind.Number, "gsxSettings.passengers.bagWeight", Suffix = "kg", Min = "0")]
    public double WeightPerBagKg { get; set; } = 15;
}
