namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// Settings for GSX Pro integration. The Couatl Remote API port is intentionally NOT configurable
/// here — it is always read from CouatlAddons.ini (see docs/integrations/gsx.md).
/// </summary>
public sealed class GsxOptions
{
    public const string SectionName = "gsx";

    /// <summary>Master switch for the GSX ground automation pillar.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Delay between Remote API reconnect attempts.</summary>
    public int ReconnectIntervalMs { get; set; } = 5000;

    /// <summary>How long to await a command result before synthesizing a timeout.</summary>
    public int CommandTimeoutMs { get; set; } = 10_000;

    /// <summary>How long to wait for a menu to appear (menuShown + matching title).</summary>
    public int MenuOpenTimeoutMs { get; set; } = 5000;

    /// <summary>Default budget for verifying a menu pick's observable effect (per-intent
    /// overridable — the reposition submenu is known to exceed 5 s).</summary>
    public int IntentVerifyTimeoutMs { get; set; } = 5000;

    // ---- Ground automation ----

    /// <summary>Master switch for the automation layer (question answering + service
    /// sequencing). The Remote API client/diagnostics run regardless.</summary>
    public bool AutomationEnabled { get; set; } = true;

    /// <summary>Start the departure service sequence automatically once preconditions hold
    /// (off by default — <c>StartDepartureServices</c> can always be invoked explicitly).</summary>
    public bool AutoStartDepartureServices { get; set; }

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
    public int TriggerConfirmTimeoutMs { get; set; } = 10_000;

    /// <summary>Hold Refueling/Boarding until the SimBrief OFP is imported into ProSim.</summary>
    public bool RequireOfpBeforeDeparture { get; set; } = true;

    // ---- Voice control ----

    /// <summary>Voice phrases for GSX ground services ("request boarding", "cockpit to
    /// ground", …) routed through the same named commands the web UI and Stream Deck use.
    /// On by default; the speech pillar itself must also be running.</summary>
    public bool VoiceControlEnabled { get; set; } = true;

    // ---- Question answering ----

    public bool SkipFollowMe { get; set; } = true;

    /// <summary>Answer "board crew"/"deboard crew" questions automatically.</summary>
    public bool AnswerCrewQuestions { get; set; } = true;

    /// <summary>GSX 4's crew menu offers "Nobody" | "Crew" | "Pilots" | "Both" (smoke-test
    /// verified — it is no longer yes/no). This exact entry is picked.</summary>
    public string CrewBoardingAnswer { get; set; } = "Both";

    /// <summary>Write the CREW/PILOTS_NOT_(DE)BOARDING LVARs so GSX never asks the crew
    /// question at all (the predecessor's SkipCrewQuestion). Off by default — the menu answer
    /// above already handles it; turn this on to skip crew boarding entirely.</summary>
    public bool SkipCrewBoardingQuestion { get; set; }

    /// <summary>Answer the "Do you want to request …" pushback confirmation with yes.</summary>
    public bool ConfirmPushbackRequest { get; set; } = true;

    /// <summary>Answer to GSX's "Attach Pushback Tug" question during boarding: "ignore"
    /// leaves the menu for the user; "no" | "yes" pick positionally (predecessor rule:
    /// yes = entry 1, no = entry 2 — the menu is not literal yes/no text).</summary>
    public string TugQuestionAnswer { get; set; } = "no";

    /// <summary>When the tug attached during boarding (answered "yes" above or attached by
    /// hand), auto-call Pushback: "never" | "afterDepartureServices" | "afterFinalLoadsheet"
    /// (predecessor default).</summary>
    public string CallPushbackWhenTugAttached { get; set; } = "afterFinalLoadsheet";

    /// <summary>Direction auto-picked when GSX raises the "Select pushback direction" menu:
    /// "straight" | "tailLeft" | "tailRight" (Prosim2GSX's tri-state, set from the OFP page's
    /// Korry buttons). Matching is by entry text; no match leaves the menu for the user.</summary>
    public string PushbackPreference { get; set; } = "straight";

    /// <summary>Accept the de-icing offer and select fluid automatically.</summary>
    public bool AutoDeIce { get; set; }

    /// <summary>"Type I" | "Type II" | "Type IV" — matched as a token in the fluid menu entry.</summary>
    public string DeIceFluidType { get; set; } = "Type IV";

    /// <summary>"75" | "100" — concentration token matched in the fluid menu entry.</summary>
    public string DeIceConcentration { get; set; } = "75";

    /// <summary>Ordered operator keywords for handling/catering operator menus; the
    /// "[GSX choice]" token is always an accepted fallback. Empty list ⇒ menus are left for
    /// the user unless <see cref="AutoSelectOperator"/> suppresses them.</summary>
    public List<string> OperatorPreferences { get; set; } = [];

    /// <summary>Write handler.set autoSelectOperator once per gate session so the operator
    /// popup never surfaces (falls back to the operator menu handler when unsupported).</summary>
    public bool AutoSelectOperator { get; set; } = true;

    /// <summary>Arrival gate to arm automatically when reaching flight (e.g. "B12"); empty ⇒
    /// none (a gate can still be armed from the GSX diagnostics page).</summary>
    public string ArrivalGate { get; set; } = "";

    /// <summary>ICAO prefixes (1–4 letters) identifying company-hub airports, matched against
    /// the loaded airport for the CompanyHub/NonCompanyHub departure-service constraints.</summary>
    public List<string> CompanyHubs { get; set; } = [];

    // ---- ProSim sync modules ----

    /// <summary>Step ProSim fuel toward the target while the GSX hose is connected.</summary>
    public bool RefuelSyncEnabled { get; set; } = true;

    /// <summary>Fuel transfer rate in kg per second.</summary>
    public double RefuelRateKgPerSec { get; set; } = 25;

    /// <summary>Rate model: "fixedRate" pumps at <see cref="RefuelRateKgPerSec"/>;
    /// "dynamicRate" computes the rate once per transfer so the fill takes about
    /// <see cref="RefuelTimeTargetSeconds"/> regardless of the ordered amount.</summary>
    public string RefuelMethod { get; set; } = "fixedRate";

    /// <summary>Target fill duration in seconds for the dynamic rate method.</summary>
    public int RefuelTimeTargetSeconds { get; set; } = 300;

    /// <summary>Skip the fuel transfer when FOB already meets the planned figure (tankering) —
    /// predecessor default on, 25 kg tolerance.</summary>
    public bool SkipRefuelOnTankering { get; set; } = true;

    /// <summary>Snap the FOB to the target when the GSX fuel hose disconnects mid-transfer,
    /// instead of pausing and waiting for it to reconnect.</summary>
    public bool RefuelFinishOnHose { get; set; }

    /// <summary>Allow the sync to pump fuel DOWN when the target is below the current quantity.
    /// Off by default: ProSim's fuel-target datarefs have twice exposed transfer amounts instead
    /// of totals, and an erroneous low target must hold (decision-logged), never defuel.</summary>
    public bool AllowDefuel { get; set; }

    /// <summary>Mirror GSX boarding counters into ProSim pax zones and cargo holds.</summary>
    public bool BoardingSyncEnabled { get; set; } = true;

    /// <summary>Place GPU + chocks (+ PCA per <see cref="PcaMode"/>) at session start on the
    /// ground, and remove them when the beacon comes on.</summary>
    public bool AutoGroundEquipment { get; set; } = true;

    /// <summary>Also place/remove preconditioned air with the ground equipment (legacy bool —
    /// superseded by <see cref="PcaMode"/> and only consulted when that is empty).</summary>
    public bool AutoPca { get; set; }

    /// <summary>PCA tri-state (predecessor ConnectPca): "never" | "always" | "onlyJetway"
    /// (place only at jetway stands, detected from GSX's jetway availability). Empty falls
    /// back to the legacy <see cref="AutoPca"/> bool.</summary>
    public string PcaMode { get; set; } = "";

    /// <summary>Disconnect PCA at session start when it is connected (e.g. from a saved panel
    /// state) but not allowed by <see cref="PcaMode"/> (predecessor PcaOverride).</summary>
    public bool PcaOverride { get; set; } = true;

    /// <summary>Connect the GPU at session start even when the APU is already running; off
    /// skips the GPU on APU power (predecessor ConnectGpuWithApuRunning).</summary>
    public bool ConnectGpuWithApuRunning { get; set; } = true;

    /// <summary>Condition-driven equipment removal during the pushback phase (predecessor
    /// GradualGroundEquipRemoval): the GPU clears as soon as external power is off the buses,
    /// the chocks once the park brake is set and the GPU is gone. Only meaningful with the
    /// beacon-orchestrated sequence OFF (the sequence owns its own timed removal).</summary>
    public bool GradualGroundEquipRemoval { get; set; }

    /// <summary>Randomized delay before chocks are placed after arriving stably parked
    /// (predecessor ChockDelayMin/Max), seconds.</summary>
    public int ChockDelayMinSec { get; set; } = 10;

    public int ChockDelayMaxSec { get; set; } = 20;

    /// <summary>Operate the jetway (or call stairs at jetway-less gates) automatically once per
    /// gate session at SESSION START, after checking they are not already connected
    /// (predecessor CallJetwayStairsOnPrep).</summary>
    public bool AutoConnectJetwayOrStairs { get; set; } = true;

    /// <summary>Connect the jetway/stairs when the departure services start, for sessions
    /// where the session-start connect is off (predecessor CallJetwayStairsDuringDeparture).</summary>
    public bool CallJetwayStairsDuringDeparture { get; set; }

    /// <summary>Remove the stairs once every departure service completed: "never" | "always" |
    /// "onlyJetway" (only when a jetway also serves the pax doors — predecessor default).</summary>
    public string RemoveStairsAfterDeparture { get; set; } = "onlyJetway";

    /// <summary>Retract jetway AND stairs when the final loadsheet is transmitted (predecessor
    /// RemoveJetwayStairsOnFinal; ignored while the beacon sequence owns removal timing).</summary>
    public bool RemoveJetwayStairsOnFinal { get; set; } = true;

    /// <summary>Connect the jetway/stairs on arrival once stably parked (predecessor
    /// CallJetwayStairsOnArrival).</summary>
    public bool CallJetwayStairsOnArrival { get; set; } = true;

    /// <summary>Run GSX's "Reposition Aircraft" once at session start on the ground.</summary>
    public bool AutoReposition { get; set; } = true;

    /// <summary>
    /// Re-anchor GSX's remembered parking to the stand the aircraft actually occupies (a
    /// gate.select for the current gate) during ground preparation. GSX persists its assigned
    /// facility across sim sessions; starting a new flight at a different stand otherwise
    /// leaves it split between two gates and every service trigger is silently dropped
    /// (issue #44: previous session ended at D5, new flight spawned at D27).
    /// </summary>
    public bool AnchorDepartureGate { get; set; } = true;

    /// <summary>Turn off ProSim's own GSX auto-integration flags (efb.gsx.*) while this
    /// application drives GSX — prevents the two automations fighting each other.</summary>
    public bool DisableProsimNativeGsx { get; set; } = true;

    // ---- Door automation ----

    /// <summary>Drive ProSim aircraft doors from GSX activity: cargo doors open for
    /// boarding/deboarding and close when loading finishes, catering (service) door toggles
    /// follow GSX's requests, and entry doors open when stairs dock at jetway-less stands.</summary>
    public bool DoorAutomationEnabled { get; set; } = true;

    /// <summary>Write L:FSDT_GSX_DISABLE_DOORS_MSG so GSX never shows "waiting for your action"
    /// door prompts while this application drives the doors. Re-asserted after Couatl restarts
    /// (the LVAR resets to 0).</summary>
    public bool SuppressGsxDoorMessages { get; set; } = true;

    /// <summary>Delay before the cargo doors open once boarding/deboarding begins.</summary>
    public int CargoDoorOpenDelaySec { get; set; } = 2;

    /// <summary>Delay before a cargo door closes after its GSX loader finishes.</summary>
    public int CargoDoorCloseDelaySec { get; set; } = 16;

    /// <summary>Leave the cargo doors open after deboarding completes (ground crew realism
    /// option; default closes them).</summary>
    public bool KeepCargoDoorsOpenAfterUnload { get; set; }

    /// <summary>Pax doors follow GSX stairs docking/leaving (predecessor DoorStairHandling —
    /// L4 always, L1 only at jetway-less stands).</summary>
    public bool DoorStairHandling { get; set; } = true;

    /// <summary>Starboard service doors follow GSX catering requests (predecessor
    /// DoorCateringHandling — the SERVICE_n toggles).</summary>
    public bool DoorCateringHandling { get; set; } = true;

    /// <summary>Cargo doors follow GSX boarding/deboarding (predecessor DoorCargoHandling —
    /// the CARGO_n toggles plus loader-finished closes).</summary>
    public bool DoorCargoHandling { get; set; } = true;

    /// <summary>Open both cargo doors when boarding/deboarding becomes active (predecessor
    /// DoorOpenBoardActive; the open delay is <see cref="CargoDoorOpenDelaySec"/>).</summary>
    public bool DoorOpenOnBoardingActive { get; set; } = true;

    /// <summary>Skip the loader-finished cargo-door close during BOARDING (predecessor
    /// DoorsCargoKeepOpenOnLoaded) — the doors still close when boarding completes.</summary>
    public bool KeepCargoDoorsOpenAfterLoad { get; set; }

    /// <summary>Close every open door once the final loadsheet is transmitted and boarding has
    /// completed (predecessor CloseDoorsOnFinal; ignored while the beacon sequence owns door
    /// timing).</summary>
    public bool CloseDoorsOnFinal { get; set; } = true;

    // ---- Beacon-orchestrated pushback sequence ----

    /// <summary>Beacon on (with departure services complete) starts the orchestrated pushback
    /// prep: wait for APU → close doors → retract jetway/stairs → clear ground equipment →
    /// pushback, each step after a randomized crew-realism delay. Beacon off pauses.</summary>
    public bool BeaconPushbackSequenceEnabled { get; set; } = true;

    /// <summary>Call the GSX Pushback service automatically when the sequence reaches
    /// ready-for-push (off = the sequence prepares everything and leaves the call to you).</summary>
    public bool CallPushbackOnBeacon { get; set; } = true;

    /// <summary>Randomized delay bounds (seconds) before the doors close.</summary>
    public int SeqDoorsCloseDelayMinSec { get; set; } = 10;
    public int SeqDoorsCloseDelayMaxSec { get; set; } = 20;

    /// <summary>Randomized delay bounds (seconds) before the jetway/stairs retract.</summary>
    public int SeqJetwayRetractDelayMinSec { get; set; } = 10;
    public int SeqJetwayRetractDelayMaxSec { get; set; } = 25;

    /// <summary>Randomized delay bounds (seconds) before ground equipment is cleared.</summary>
    public int SeqGpuDisconnectDelayMinSec { get; set; } = 10;
    public int SeqGpuDisconnectDelayMaxSec { get; set; } = 20;

    // ---- Arrival / deboarding ----

    /// <summary>Mirror GSX deboarding counters into ProSim: seats empty front-first as pax
    /// leave, cargo drains by GSX's unload percentage.</summary>
    public bool DeboardingSyncEnabled { get; set; } = true;

    /// <summary>Call the GSX Deboarding service automatically once stably parked on arrival.</summary>
    public bool AutoCallDeboardOnArrival { get; set; } = true;

    /// <summary>How long the stable-parked condition (on ground, engines off, park brake set,
    /// beacon off, stationary) must hold before arrival actions fire.</summary>
    public int ArrivalStableSeconds { get; set; } = 10;

    // ---- Fuel-on-board persistence ----

    /// <summary>Restore the saved FOB for this aircraft at preparation (before a plan is
    /// loaded) and save the FOB when stably parked on arrival — so the next session starts
    /// with the fuel you landed with.</summary>
    public bool FuelSaveLoadFob { get; set; } = true;

    /// <summary>FOB written at preparation when no value has been saved for this aircraft yet.</summary>
    public double FuelResetDefaultKg { get; set; } = 3000;

    /// <summary>Saved FOB per aircraft title — maintained by the app at arrival; not intended
    /// for hand editing (but harmless to edit).</summary>
    public Dictionary<string, double> FuelFobSaved { get; set; } = [];

    // ---- Passenger randomization ----

    /// <summary>Randomize the booked pax against the OFP: each seat flips with
    /// <see cref="NoShowChancePerSeat"/> (booked→empty = no-show, empty→booked = walk-up
    /// extra), with cargo adjusted by <see cref="WeightPerBagKg"/> per passenger delta.
    /// Off by default — the load then matches the OFP exactly.</summary>
    public bool RandomizePaxNoShows { get; set; }

    /// <summary>Per-seat flip probability when <see cref="RandomizePaxNoShows"/> is on.</summary>
    public double NoShowChancePerSeat { get; set; } = 0.03;

    /// <summary>Checked-bag weight used to adjust cargo for no-shows/extras.</summary>
    public double WeightPerBagKg { get; set; } = 15;
}
