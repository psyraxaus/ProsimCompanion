namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// SayIntentions AI ATC integration: voice ATC requests spoken via the sayAs API on COM1, a
/// departure-comms gate that takes the SI copilot's comms back near the runway so the user
/// controls departure timing, and optional frequency auto-tune from getWX comms data.
/// Disabled by default — everything requires SayIntentions running with an active flight.
/// </summary>
public sealed class SayIntentionsOptions : IOptionSection
{
    public static string SectionName => "sayIntentions";

    public bool Enabled { get; set; }

    /// <summary>"auto" reads the api key from flight.json; "manual" uses <see cref="ManualApiKey"/>.</summary>
    public string ApiKeySource { get; set; } = "auto";

    public string ManualApiKey { get; set; } = "";

    /// <summary>Auto-tune the matched station frequency before transmitting a request.</summary>
    public bool AutoTuneFrequency { get; set; }

    /// <summary>Take comms back from the SI copilot approaching the runway.</summary>
    public bool DepartureGatingEnabled { get; set; } = true;

    /// <summary>Distance to the runway (nm) at which the departure gate trips.</summary>
    public double DepartureGateDistanceNm { get; set; } = 0.3;

    /// <summary>"icao" or "faa" request phraseology.</summary>
    public string Phraseology { get; set; } = "icao";

    /// <summary>The FO speaks the exact transmission text locally (in the FO voice) before it
    /// goes to sayAs — the audible half of the positive-feedback fix (issue #52; the silent
    /// pushback-clearance defect). Turn OFF if SayIntentions itself voices sayAs audibly on
    /// this setup (live-verify) — otherwise the call is heard twice. The FO's short "Roger —
    /// calling …" acknowledgement is not optional; it is the defect fix itself.</summary>
    public bool FoSpeaksTransmission { get; set; } = true;

    // ---- Wrong-frequency report ----

    /// <summary>The FO says once when COM1 has stayed off the frequency SayIntentions ATC
    /// last assigned (its "Contact … on …" instruction). SayIntentions itself never objects —
    /// it lets you check in with whichever station you tuned (live capture 2026-10-05).
    /// Quiet at the gate and whenever the assignment cannot be read.</summary>
    public bool FrequencyMonitorEnabled { get; set; } = true;

    /// <summary>How long COM1 may stay on another frequency before the report — long enough
    /// for a normal hand-off and for dialling through channels. Floor 10 s.</summary>
    public int FrequencyMonitorWaitSeconds { get; set; } = 60;

    // ---- Arrival gate from ATC ----

    /// <summary>Take the gate SayIntentions ATC assigns (flight.json <c>assigned_gate</c>) as
    /// the GSX arrival gate (2026-10-08). Only a gate that differs from the departure stand
    /// and appears from the climb onward counts; a gate you queued yourself always wins; past
    /// the cruise entry the gate goes to GSX at once. Off by default — SayIntentions' gate
    /// pick may not exist in the loaded scenery, and GSX's own menu is then left for you.</summary>
    public bool ArrivalGateFromAtc { get; set; }

    /// <summary>Which ProSim push-to-talk switch keys SayIntentions (2026-10-10, the sidestick
    /// PTT on the second PC bound to a ProSim dataref): <c>none</c> (default),
    /// <c>captainSidestick</c>, <c>foSidestick</c>, <c>captainHandMic</c>, <c>foHandMic</c>,
    /// <c>observerHandMic</c>. Each edge writes <see cref="PttLvar"/> (1 pushed, 0 released)
    /// through SimConnect — the SayIntentions client's "Map PTT inside the sim with an LVAR".
    /// No joystick polling involved.</summary>
    public string PttSource { get; set; } = "none";

    /// <summary>The SayIntentions control LVAR to key: <c>L:SIAI_CONTROL_PTT_COM</c> (the
    /// selected radio), <c>…_COM1</c> / <c>…_COM2</c> (forced), <c>…_INTERCOM1..3</c>,
    /// <c>…_GROUP</c> — the names the client lists under Controls.</summary>
    public string PttLvar { get; set; } = "L:SIAI_CONTROL_PTT_COM";

    // ---- Batch weather (ATIS/METAR/TAF) + CPDLC station ----
    // These need only the API key, NOT an active flight — a parked cockpit can still pull
    // weather while SayIntentions itself is between flights.

    /// <summary>Fetch ATIS/METAR/TAF + the CPDLC logon for the OFP airports (web Weather page,
    /// composite weather-provider backfill).</summary>
    public bool WeatherEnabled { get; set; } = true;

    /// <summary>Cache TTL — entries younger than this are served without touching the API.</summary>
    public int WeatherCacheMinutes { get; set; } = 10;

    /// <summary>Forced-refresh debounce — protects the API from button-spam and concurrent
    /// browser clients (predecessor-proven policy).</summary>
    public int WeatherRefreshDebounceSeconds { get; set; } = 30;
}
