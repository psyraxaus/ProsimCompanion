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

/// <summary>One push-to-talk input binding (Prosim2FO's InputBinding shape): a keyboard key
/// OR a joystick button, never both — capture assigns whichever the user presses first.
/// The joystick device is bound by product name (ids shuffle on re-plug); the numeric id is
/// kept as display key and fallback.</summary>
public sealed class PttBindingOptions
{
    /// <summary>"keyboard", "joystickButton", or "" for unset.</summary>
    public string Kind { get; set; } = "";

    /// <summary>Keyboard kind: key name/VK code in the PTT parser's vocabulary.</summary>
    public string Key { get; set; } = "";

    /// <summary>Joystick kind: winmm id (fallback), product name (authoritative), button.</summary>
    public int? JoystickDevice { get; set; }
    public string JoystickDeviceName { get; set; } = "";
    public int? Button { get; set; }

    public bool IsSet => Kind.Length > 0;
}

/// <summary>Human pacing for the virtual pilot's button pushing (Prosim2FO's Humanize):
/// inter-key gaps are randomized with occasional longer "scan" pauses biased toward keys
/// that change the MCDU page. Configured step delays are functional minimums (page-change
/// time) — humanization only ever ADDS time, never undercuts them.</summary>
public sealed class HumanizeOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Overall pacing multiplier for inter-key gaps (1.0 = normal, 2.0 = twice as slow).</summary>
    public double Tempo { get; set; } = 1.0;

    /// <summary>Max random stretch of each inter-key gap (0.8 = 1.0–1.8× the configured delay).</summary>
    public double GapJitter { get; set; } = 0.8;

    /// <summary>± fraction applied to each key's hold time (0.35 = 0.65–1.35× holdMs).</summary>
    public double HoldJitter { get; set; } = 0.35;

    /// <summary>Chance of an extra "scan/think" pause after a key (tripled after page keys).</summary>
    public double ThinkPauseChance { get; set; } = 0.15;

    public int ThinkPauseMinMs { get; set; } = 300;
    public int ThinkPauseMaxMs { get; set; } = 1200;
}

/// <summary>
/// Settings for the voice First Officer pillar's speech foundations: the arbiter, the TTS
/// provider chain (fixed order Kokoro → Google → WinRT → SAPI5, docs/integrations/speech.md)
/// and audio playback. Every provider is optional — the router skips unconfigured or failing
/// providers, and <see cref="LocalOnly"/> is the hard switch that keeps synthesis off the
/// network entirely (Google is then excluded from every path, including voice listing).
/// </summary>
/// <summary>HTTP shape of the LAN speech-recognition server (<c>speech.asrApi</c>).</summary>
public enum AsrApiKind
{
    /// <summary>The Prosim2FO faster-whisper wrapper (<c>deploy/faster-whisper</c>):
    /// <c>POST /transcribe</c> with a <c>hotwords</c> field, flat JSON reply carrying
    /// <c>text</c>, <c>confidence</c> and <c>no_speech_prob</c>.</summary>
    FasterWhisper,

    /// <summary>whisper.cpp <c>whisper-server</c>: OpenAI-style
    /// <c>POST /v1/audio/transcriptions</c> with a <c>prompt</c> field; the app asks for
    /// <c>verbose_json</c> and derives confidence from the segments' <c>avg_logprob</c>.</summary>
    WhisperCpp,
}

public sealed class SpeechOptions : IOptionSection
{
    public static string SectionName => "speech";

    /// <summary>Master switch for the speech pillar.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Which seat the HUMAN flies from ("left" or "right"). The virtual pilot
    /// operates the opposite side: with the default "left", the FO uses CDU2 / the FO-side
    /// flight controls / EFIS2; "right" flips every side-dependent surface (CDU1, the
    /// captain-side controls, EFIS1). The safety rule is seat-relative — the virtual pilot
    /// never moves the human's controls.</summary>
    public string PilotSeat { get; set; } = "left";

    /// <summary>Human-like pacing for MCDU/FCU button sequences.</summary>
    public HumanizeOptions Humanize { get; set; } = new();

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

    /// <summary>Kokoro connect / first-byte timeout (floor 200 ms at use) — deliberately
    /// tight so a sleeping LAN host does not stall a callout; the router falls through
    /// instead. Covers only the wait for response headers (issue #113): the streamed WAV
    /// body has its own budget, <see cref="KokoroBodyTimeoutMs"/>.</summary>
    public int KokoroTimeoutMs { get; set; } = 1500;

    /// <summary>Base budget for reading the streamed WAV body once Kokoro has answered
    /// (issue #113). kokoro-fastapi synthesises while it streams, so a long briefing takes
    /// seconds; the effective budget is this plus 100 ms per character, never below the
    /// connect timeout. A healthy host asked to say something long must not be cooled down.</summary>
    public int KokoroBodyTimeoutMs { get; set; } = 15000;

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

    // ---- Recognition (chain: LAN whisper → WinRT → System.Speech) ----

    /// <summary>Wire shape of the LAN ASR server at <see cref="AsrBaseUrl"/>. The two
    /// flavours disagree on the transcribe path, the biasing field and the response JSON;
    /// pointing the faster-whisper shape at a whisper.cpp server answers 404 to every
    /// utterance while <c>/health</c> still says OK (2026-08-29 — voice went silent with no
    /// log evidence).</summary>
    public AsrApiKind AsrApi { get; set; } = AsrApiKind.FasterWhisper;

    /// <summary>Server root of the LAN ASR server (docs/integrations/speech.md), e.g.
    /// <c>http://192.168.1.50:8000</c>; empty disables the LAN engine.</summary>
    public string AsrBaseUrl { get; set; } = "";

    /// <summary>Transcription request timeout.</summary>
    public int AsrTimeoutMs { get; set; } = 8000;

    /// <summary>Capture device product-name (prefix match); empty uses the default mic.</summary>
    public string InputDevice { get; set; } = "";

    /// <summary>Voice-activity engine for LAN ASR segmentation: "silero" (default — Silero
    /// VAD over ONNX Runtime, robust to cockpit/engine noise and TTS bleed) or "rms" (the
    /// legacy energy gate). Silero degrades to RMS automatically if the model or the native
    /// runtime cannot load — recognition is never lost to the VAD.</summary>
    public string VadEngine { get; set; } = "silero";

    /// <summary>Speech probability that opens an utterance (Silero path only). Hysteresis is
    /// built in: trailing silence is only counted below this minus 0.15 (Silero's default
    /// negative threshold), so a soft word tail can't end a sentence early.</summary>
    public double VadThreshold { get; set; } = 0.5;

    /// <summary>Trailing silence that closes an utterance, Silero path only — the RMS gate
    /// keeps its proven 700 ms regardless. Shorter than the legacy value because Silero
    /// doesn't mistake quiet speech for silence, so commands land sooner.</summary>
    public int VadEndSilenceMs { get; set; } = 500;

    /// <summary>Segments shorter than this are dropped as blips (door slams, breaths),
    /// Silero path only.</summary>
    public int VadMinSpeechMs { get; set; } = 300;

    /// <summary>Pre-speech audio retained ahead of the first speech frame so a soft first
    /// syllable isn't clipped, Silero path only.</summary>
    public int VadPreRollMs { get; set; } = 300;

    /// <summary>Hard cap on one utterance, Silero path only; a capped utterance is posted and
    /// segmentation restarts immediately.</summary>
    public int VadMaxUtteranceMs { get; set; } = 15_000;

    /// <summary>FO push-to-talk binding (key OR joystick button, Prosim2FO process). When
    /// unset, the legacy flat fields below still apply, so pre-binding configs migrate
    /// silently.</summary>
    public PttBindingOptions PttBinding { get; set; } = new();

    /// <summary>ATC-mute binding — same shape; while held the FO ignores everything.</summary>
    public PttBindingOptions AtcMuteBinding { get; set; } = new();

    /// <summary>LEGACY push-to-talk key (pre-binding configs): a virtual-key name ("F12",
    /// "RightCtrl", "Space", a letter) or a decimal VK code; empty disables keyboard PTT.
    /// Ignored once <see cref="PttBinding"/> is set.</summary>
    public string PttKey { get; set; } = "";

    /// <summary>Joystick PTT: winmm joystick id (0–15) and button index (0–31); a null button
    /// (or no device) disables joystick PTT.</summary>
    public int? PttJoystickDevice { get; set; }
    public int? PttJoystickButton { get; set; }

    /// <summary>Product name of the FO PTT joystick. When set it wins over the numeric id —
    /// winmm ids shuffle when devices are re-plugged, product names don't. Matched on prefix
    /// both ways (winmm truncates names to 31 chars).</summary>
    public string PttJoystickDeviceName { get; set; } = "";

    /// <summary>ATC push-to-talk key — while held, the FO stops listening (a suppression, not
    /// a mode change); empty disables.</summary>
    public string AtcMuteKey { get; set; } = "";

    /// <summary>ATC-mute joystick binding — for pilots whose ATC transmit is a stick/yoke
    /// button rather than a key. Same semantics as the key: while the button is held the FO
    /// ignores everything it hears. Same id/name/button rules as the FO PTT binding.</summary>
    public int? AtcMuteJoystickDevice { get; set; }
    public int? AtcMuteJoystickButton { get; set; }
    public string AtcMuteJoystickDeviceName { get; set; } = "";

    /// <summary>"pushToTalk" (default) or "continuous" — continuous listens whenever a window
    /// is open, no PTT needed.</summary>
    public string RecognitionMode { get; set; } = "pushToTalk";

    // ---- Interphone transmit gating (issue #72) ----

    /// <summary>Latch crew hail dialogues to the captain ACP transmit selector
    /// (S_ASP_SEND_CHANNEL): "cockpit to ground" needs INT selected, "cockpit to crew"
    /// needs CAB, and moving the selector off the channel mid-dialogue hangs up — the
    /// realism the receive-side latches alone cannot give. Degrades to always-accept when
    /// the selector dataref is absent or stale (older ProSim, degraded mode).</summary>
    public bool AcpTransmitGating { get; set; } = true;

    /// <summary>Additionally require the momentary ACP INT key (S_ASP_INT_SEND) pushed at
    /// the moment a GROUND hail is spoken; the selector latch then keeps the dialogue open.
    /// Off by default — most home panels drive only the selector.</summary>
    public bool AcpIntKeyRequired { get; set; }

    /// <summary>Minimum engine confidence for offline recognizers.</summary>
    public double RecognitionConfidenceThreshold { get; set; } = 0.6;

    /// <summary>Phonetic snapping: 0.5·Levenshtein + 0.5·DoubleMetaphone must clear this.</summary>
    public double SnappingThreshold { get; set; } = 0.7;

    /// <summary>Interpreter acoustic gates (command windows only): reject when no_speech_prob
    /// is at/above the ceiling, or acoustic confidence below the floor (0 disables).</summary>
    public double NoSpeechCeiling { get; set; } = 0.6;
    public double ConfidenceFloor { get; set; } = 0.5;

    /// <summary>Utterances absorbed silently instead of chirping a reject (issue #120):
    /// whisper's classic silence/breath hallucinations. Matched against the normalized heard
    /// text, whole-utterance only, and only on the reject path — a phrase here can still be
    /// a valid checklist answer ("checked") because answers are consumed before rejection.
    /// Power-user list: edit in settings.json (speech.asrHallucinationPhrases).</summary>
    public List<string> AsrHallucinationPhrases { get; set; } =
    [
        "thank you", "thank you very much", "thanks", "you", "bye", "goodbye",
        "okay", "checked", "sniff", "blank audio", "silence",
    ];

    /// <summary>Snapped command scores in [threshold, this) trigger a "did you mean …?"
    /// confirmation instead of firing.</summary>
    public double ConfirmBelowScore { get; set; } = 0.85;

    /// <summary>Play the short listening cue tone when a window opens for a reply.</summary>
    public bool ListeningTone { get; set; } = true;

    /// <summary>FO responses to spoken engine-start and flap calls ("starting engine two",
    /// "flaps two" → placard speed check). Verbal only — the FO never moves the flap lever
    /// or engine masters (issue #67; actuation is deferred pending write-safety review).</summary>
    public bool EngineFlapCallouts { get; set; } = true;

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
