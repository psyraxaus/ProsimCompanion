namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// SayIntentions AI ATC integration: voice ATC requests spoken via the sayAs API on COM1, a
/// departure-comms gate that takes the SI copilot's comms back near the runway so the user
/// controls departure timing, and optional frequency auto-tune from getWX comms data.
/// Disabled by default — everything requires SayIntentions running with an active flight.
/// </summary>
public sealed class SayIntentionsOptions
{
    public const string SectionName = "sayIntentions";

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
