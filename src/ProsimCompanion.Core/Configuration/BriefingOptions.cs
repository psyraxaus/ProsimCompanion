namespace ProsimCompanion.Core.Configuration;

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

    /// <summary>Manual procedure overrides used when the FMS plan doesn't resolve a field.</summary>
    public string DepartureAirport { get; set; } = "";
    public string DepartureRunway { get; set; } = "";
    public string DepartureSid { get; set; } = "";
    public string ArrivalAirport { get; set; } = "";
    public string ArrivalRunway { get; set; } = "";
    public string ArrivalStar { get; set; } = "";
    public string ArrivalApproach { get; set; } = "";

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
