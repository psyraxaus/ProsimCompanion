namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// Post-flight debrief: a spoken summary of the session's logged facts, delivered at shutdown.
/// The deterministic template is always the floor; optional LLM styling (behind the number
/// verifier, sharing the briefing's <c>Llm*</c> endpoint settings) rephrases it warmly without
/// ever inventing a number.
/// </summary>
public sealed class DebriefOptions : IOptionSection
{
    public static string SectionName => "debrief";

    public bool Enabled { get; set; } = true;

    /// <summary>"full" (default) or "brief" — brief omits the activity counts and fuel-used lines.</summary>
    public string Verbosity { get; set; } = "full";

    /// <summary>Style the debrief with the LLM when the briefing's LLM is enabled and a model
    /// is configured. Off = always the deterministic template. This is the debrief's own
    /// opt-out; the endpoint itself lives in the briefing section's <c>llm*</c> keys.</summary>
    public bool UseLlm { get; set; } = true;
}
