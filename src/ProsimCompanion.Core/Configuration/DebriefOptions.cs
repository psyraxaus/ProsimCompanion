namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// Post-flight debrief: a deterministic spoken summary of the session's logged facts,
/// delivered at shutdown. Deliberately template-only in this slice — LLM styling of the
/// wording (with number verification) is a deferred follow-up, so every number spoken today
/// traces verbatim to a session event.
/// </summary>
public sealed class DebriefOptions
{
    public const string SectionName = "debrief";

    public bool Enabled { get; set; } = true;

    /// <summary>"full" (default) or "brief" — brief omits the activity counts and fuel-used lines.</summary>
    public string Verbosity { get; set; } = "full";
}
