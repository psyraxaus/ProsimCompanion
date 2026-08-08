namespace ProsimCompanion.Core.Configuration;

/// <summary>Where briefing procedure identifiers (runway/SID/STAR/approach) are resolved from
/// (Prosim2FO semantics).</summary>
public enum ProcedureSourceMode
{
    /// <summary>Per-field walk: FMS (flightPlanXml, then configured datarefs) → SayIntentions
    /// flight.json → the manual entries.</summary>
    Auto,

    /// <summary>Use only the manual entries.</summary>
    Manual,

    /// <summary>Use only SayIntentions flight.json.</summary>
    FlightJson,
}

/// <summary>Wake-on-LAN for the self-hosted LLM box (Prosim2FO parity). The app can only
/// SEND the magic packet — WoL must already be enabled in the target's BIOS/UEFI and NIC
/// driver (one-time manual setup), and the target needs wired Ethernet on this subnet
/// (directed broadcasts don't cross routers; WoL over WiFi is unreliable).</summary>
public sealed class WakeOnLanOptions
{
    /// <summary>Send a magic packet at startup to power on the host.</summary>
    public bool Enabled { get; set; }

    /// <summary>Target NIC MAC address; ':' or '-' separators (or none).</summary>
    public string MacAddress { get; set; } = "";

    /// <summary>"255.255.255.255" or the subnet broadcast, e.g. "192.168.1.255".</summary>
    public string BroadcastAddress { get; set; } = "255.255.255.255";

    /// <summary>UDP port for the magic packet (conventionally 9, the discard port).</summary>
    public int Port { get; set; } = 9;
}

/// <summary>Optional ProSim FMS dataref names for the per-field FMS procedure tier — blank
/// means that field is unavailable from a dedicated dataref (the flightPlanXml parse still
/// applies). Kept configurable because these names vary across ProSim builds.</summary>
public sealed class BriefingFmsDatarefs
{
    public string OriginIcao { get; set; } = "";
    public string DestinationIcao { get; set; } = "";
    public string DepartureRunway { get; set; } = "";
    public string Sid { get; set; } = "";
    public string ArrivalRunway { get; set; } = "";
    public string Star { get; set; } = "";
    public string Approach { get; set; } = "";
}

/// <summary>
/// Voice briefing settings (departure/arrival composition from Navigraph DFD + weather, with
/// optional LLM styling behind the number verifier). Every source is optional — missing DFD,
/// FMS plan, weather, or LLM each just thin out the briefing; the deterministic template is
/// always the floor.
/// </summary>
public sealed class BriefingOptions
{
    public const string SectionName = "briefing";

    /// <summary>Path to the user-supplied Navigraph DFD SQLite database; empty disables
    /// nav-data facts (never redistributed).</summary>
    public string DfdPath { get; set; } = "";

    /// <summary>How procedure identifiers are resolved (Auto = FMS → flight.json → manual,
    /// with per-field provenance logging).</summary>
    public ProcedureSourceMode ProcedureSource { get; set; } = ProcedureSourceMode.Auto;

    /// <summary>Per-field FMS dataref names for the FMS tier (all optional).</summary>
    public BriefingFmsDatarefs FmsDatarefs { get; set; } = new();

    /// <summary>Manual procedure overrides used when the FMS plan doesn't resolve a field.</summary>
    public string DepartureAirport { get; set; } = "";
    public string DepartureRunway { get; set; } = "";
    public string DepartureSid { get; set; } = "";
    public string ArrivalAirport { get; set; } = "";
    public string ArrivalRunway { get; set; } = "";
    public string ArrivalStar { get; set; } = "";
    public string ArrivalApproach { get; set; } = "";

    // ---- Missed-approach re-brief (Prosim2FO parity; safety content, never persona-styled) ----

    /// <summary>Speak the published missed-approach legs automatically after a go-around.</summary>
    public bool MissedApproachRebriefEnabled { get; set; } = true;

    /// <summary>Seconds after the go-around before the re-brief may speak (workload guard);
    /// it additionally waits for gear-up.</summary>
    public int MissedApproachDelaySeconds { get; set; } = 15;

    /// <summary>Hard backstop: speak at this many seconds even if gear-up is never seen.</summary>
    public int MissedApproachHardCeilingSeconds { get; set; } = 30;

    /// <summary>Wake-on-LAN for the LLM host: magic packet at startup (fire-and-forget —
    /// readiness is confirmed by reaching the endpoint, never by the send).</summary>
    public WakeOnLanOptions LlmWakeOnLan { get; set; } = new();

    // ---- Interactive minima capture (arrival-brief sub-dialogue) ----

    /// <summary>Capture the minima by voice during the arrival brief (prompt → read-back →
    /// confirm). A value already entered on the web page skips the dialogue; disabled means
    /// the brief just echoes whatever the store holds.</summary>
    public bool MinimaCaptureEnabled { get; set; } = true;

    /// <summary>Listening window per capture attempt.</summary>
    public int MinimaListenTimeoutSeconds { get; set; } = 15;

    /// <summary>Failed/unconfirmed attempts before the brief proceeds "not briefed".</summary>
    public int MinimaMaxRetries { get; set; } = 2;

    /// <summary>Verify every number in LLM output against the source facts; a failed retry
    /// falls back to the deterministic template.</summary>
    public bool VerifyNumbers { get; set; } = true;

    // ---- Optional OpenAI-compatible LLM (briefing prose only — numbers are verified) ----

    public bool LlmEnabled { get; set; }
    public string LlmBaseUrl { get; set; } = "http://localhost:3000/api";
    public string LlmApiKey { get; set; } = "";
    public string LlmModel { get; set; } = "";
    public int LlmMaxTokens { get; set; } = 512;
    public int LlmTimeoutSeconds { get; set; } = 30;
}
