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

    /// <summary>The out-of-the-box departure order, used both as the property default (for the
    /// settings-defaults writer) and to restore the list when the settings file omits it.</summary>
    public static IReadOnlyList<string> DefaultDepartureServiceOrder { get; } =
        ["Refueling", "Catering", "Water", "Lavatory", "Cleaning", "Boarding"];

    /// <summary>Departure services in trigger order (canonical Remote API ids). Remove entries
    /// to disable a service.</summary>
    public List<string> DepartureServiceOrder { get; set; } = [.. DefaultDepartureServiceOrder];

    /// <summary>Run non-boarding departure services concurrently (refuel + catering + … all
    /// called as soon as each is callable). Off = strict one-at-a-time in order.</summary>
    public bool ConcurrentServices { get; set; } = true;

    /// <summary>Services that must complete before Boarding is called. Empty (default) = all
    /// other services in <see cref="DepartureServiceOrder"/> (the classic "board last"); name
    /// specific services (e.g. ["Refueling"]) to board once just those are done.</summary>
    public List<string> BoardingAfter { get; set; } = [];

    /// <summary>Hold Refueling/Boarding until the SimBrief OFP is imported into ProSim.</summary>
    public bool RequireOfpBeforeDeparture { get; set; } = true;

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

    // ---- ProSim sync modules ----

    /// <summary>Step ProSim fuel toward the target while the GSX hose is connected.</summary>
    public bool RefuelSyncEnabled { get; set; } = true;

    /// <summary>Fuel transfer rate in kg per second.</summary>
    public double RefuelRateKgPerSec { get; set; } = 25;

    /// <summary>Allow the sync to pump fuel DOWN when the target is below the current quantity.
    /// Off by default: ProSim's fuel-target datarefs have twice exposed transfer amounts instead
    /// of totals, and an erroneous low target must hold (decision-logged), never defuel.</summary>
    public bool AllowDefuel { get; set; }

    /// <summary>Mirror GSX boarding counters into ProSim pax zones and cargo holds.</summary>
    public bool BoardingSyncEnabled { get; set; } = true;

    /// <summary>Place GPU + chocks (+ PCA per <see cref="AutoPca"/>) at session start on the
    /// ground, and remove them when the beacon comes on.</summary>
    public bool AutoGroundEquipment { get; set; } = true;

    /// <summary>Also place/remove preconditioned air with the ground equipment.</summary>
    public bool AutoPca { get; set; }

    /// <summary>Operate the jetway (or call stairs at jetway-less gates) automatically once per
    /// gate session, after checking they are not already connected.</summary>
    public bool AutoConnectJetwayOrStairs { get; set; } = true;

    /// <summary>Run GSX's "Reposition Aircraft" once at session start on the ground.</summary>
    public bool AutoReposition { get; set; } = true;

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
