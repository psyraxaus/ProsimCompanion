namespace ProsimCompanion.Core.Configuration;

/// <summary>Sterile-cockpit handling of Normal-priority speech (enum order carried from
/// Prosim2FO's SOP profile).</summary>
public enum SterileNormalPolicy
{
    /// <summary>Hold Normal items and replay them once sterile ends.</summary>
    Defer,

    /// <summary>Let Normal speech through (the default — Normal carries checklists and
    /// briefings, which are required during a low-altitude approach).</summary>
    Allow,

    /// <summary>Drop Normal items outright.</summary>
    Suppress,
}

/// <summary>
/// Settings for the voice First Officer pillar's speech foundations: the arbiter, the TTS
/// provider chain (fixed order Kokoro → Google → WinRT → SAPI5, docs/integrations/speech.md)
/// and audio playback. Every provider is optional — the router skips unconfigured or failing
/// providers, and <see cref="LocalOnly"/> is the hard switch that keeps synthesis off the
/// network entirely (Google is then excluded from every path, including voice listing).
/// </summary>
public sealed class SpeechOptions
{
    public const string SectionName = "speech";

    /// <summary>Master switch for the speech pillar.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Hard "local only" mode: network TTS providers (Kokoro, Google) are excluded
    /// regardless of their own configuration.</summary>
    public bool LocalOnly { get; set; }

    // ---- Sterile cockpit (predecessor defaults; lived in the SOP profile in Prosim2FO —
    //      moves there if/when ProsimCompanion grows SOP profiles) ----

    /// <summary>Enables the sterile-cockpit suppression rule.</summary>
    public bool SterileEnabled { get; set; } = true;

    /// <summary>Sterile ceiling in feet MSL (baro, not AGL): airborne workload phases below
    /// this gate Low/Normal speech. High/Critical always speak.</summary>
    public int SterileCockpitCeilingFt { get; set; } = 10_000;

    /// <summary>Drop Low-priority speech while sterile (never replayed).</summary>
    public bool SterileSuppressLow { get; set; } = true;

    /// <summary>What sterile does to Normal-priority speech.</summary>
    public SterileNormalPolicy SterileNormalPolicy { get; set; } = SterileNormalPolicy.Allow;

    /// <summary>Let <c>cabin.*</c>-tagged reports through while sterile — cabin-ready calls
    /// below the ceiling are operationally expected (a deliberate tag exemption rather than
    /// inflating their priority).</summary>
    public bool SterileExemptCabinReports { get; set; } = true;

    // ---- Kokoro (local neural TTS, kokoro-fastapi) ----

    /// <summary>Base URL of a kokoro-fastapi instance; empty disables the provider.</summary>
    public string KokoroBaseUrl { get; set; } = "";

    /// <summary>Model name sent to Kokoro.</summary>
    public string KokoroModel { get; set; } = "kokoro";

    /// <summary>Kokoro voice id.</summary>
    public string KokoroVoice { get; set; } = "bm_george";

    /// <summary>Kokoro request timeout (floor 200 ms at use) — deliberately tight so a
    /// sleeping LAN host does not stall a callout; the router falls through instead.</summary>
    public int KokoroTimeoutMs { get; set; } = 1500;

    /// <summary>Per-voice disk-cache cap in MB for Kokoro audio; 0 = unlimited.</summary>
    public int KokoroMaxCacheMbPerVoice { get; set; }

    // ---- Google Cloud TTS (Chirp 3 HD) ----

    /// <summary>Path to the service-account JSON key file; empty disables the provider.
    /// Never logged.</summary>
    public string GoogleKeyFilePath { get; set; } = "";

    /// <summary>Google voice name; the language code is derived from its prefix
    /// (e.g. "en-AU-Chirp3-HD-Charon" → "en-AU").</summary>
    public string GoogleVoice { get; set; } = "en-AU-Chirp3-HD-Charon";

    /// <summary>Monthly character budget: once the usage counter reaches this figure the
    /// provider refuses further synthesis for the month and the chain falls through to the
    /// offline voices. Google's free tier is 1M chars — the margin keeps an overshoot
    /// harmless. 0 disables enforcement (the predecessor tracked but never enforced).</summary>
    public int GoogleMonthlyCharBudget { get; set; } = 950_000;

    // ---- Windows fallbacks ----

    /// <summary>Preferred WinRT (Windows.Media.SpeechSynthesis) voice display name or id;
    /// empty picks the system default. WinRT is the only provider that reaches the Windows 11
    /// neural "Natural" voices.</summary>
    public string WinRtVoice { get; set; } = "";

    /// <summary>Preferred SAPI5 (System.Speech) voice name; empty picks the system default.</summary>
    public string SapiVoice { get; set; } = "";

    // ---- Playback ----

    /// <summary>Output device friendly name (exact match); empty uses the system default
    /// render device.</summary>
    public string OutputDevice { get; set; } = "";

    /// <summary>Playback volume 0–100, applied client-side in the playback chain so it works
    /// for every provider (the predecessor only honoured it for WinRT/SAPI5).</summary>
    public int Volume { get; set; } = 100;

    /// <summary>Apply the intercom band-pass colouring so the FO sounds like a headset, not a
    /// narrator.</summary>
    public bool IntercomFilter { get; set; } = true;

    /// <summary>TTS disk-cache root; empty resolves to
    /// %LOCALAPPDATA%\ProsimCompanion\cache\tts.</summary>
    public string CacheFolder { get; set; } = "";
}
