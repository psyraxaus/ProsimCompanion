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
}
