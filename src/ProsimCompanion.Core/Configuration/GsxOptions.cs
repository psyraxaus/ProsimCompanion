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

    /// <summary>Departure services in trigger order (canonical Remote API ids). Remove entries
    /// to disable a service.</summary>
    public List<string> DepartureServiceOrder { get; set; } =
        ["Refueling", "Catering", "Water", "Lavatory", "Cleaning", "Boarding"];

    /// <summary>Hold Refueling/Boarding until the SimBrief OFP is imported into ProSim.</summary>
    public bool RequireOfpBeforeDeparture { get; set; } = true;

    // ---- Question answering ----

    public bool SkipFollowMe { get; set; } = true;

    /// <summary>Answer "board crew"/"deboard crew" questions automatically.</summary>
    public bool AnswerCrewQuestions { get; set; } = true;

    /// <summary>GSX 4's crew menu offers "Nobody" | "Crew" | "Pilots" | "Both" (smoke-test
    /// verified — it is no longer yes/no). This exact entry is picked.</summary>
    public string CrewBoardingAnswer { get; set; } = "Both";

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
}
