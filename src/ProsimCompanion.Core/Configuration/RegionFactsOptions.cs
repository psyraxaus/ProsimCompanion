namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// Ambient region facts in the cruise (issue #122), <c>speech.regionFacts</c>. Now and then
/// the First Officer offers a short fact about the country or sea below — unprompted, at
/// Low priority (so "quiet please" silences it), only in the cruise, only while the flight
/// is live, never while the pilot has paused the FO's ear. The region comes from the same
/// offline atlas as "what are we flying over?" (issue #153); the fact from the user-editable
/// <c>region-facts.json</c> or, when the language model is up, from the model under the
/// place-facts prompt. Each region is spoken about at most once per flight.
/// </summary>
public sealed class RegionFactsOptions
{
    /// <summary>Master switch. Off: one log line at startup and nothing else — no timer
    /// work, no atlas lookups, no events.</summary>
    [OptionField(FieldKind.Bool, "speechSettings.briefing.regionFacts.enabled", Label = "speechSettings.common.enabled")]
    public bool Enabled { get; set; }

    /// <summary>Shortest gap between two facts, minutes. The first fact comes this long (plus
    /// the jitter below) after the cruise begins.</summary>
    [OptionField(FieldKind.Number, "speechSettings.briefing.regionFacts.shortest", Suffix = "min", Min = "1", Max = "240", DependsOn = "Enabled")]
    public int MinIntervalMinutes { get; set; } = 15;

    /// <summary>Longest gap between two facts, minutes; each gap is drawn at random between
    /// the two so the cadence never feels like a timer.</summary>
    [OptionField(FieldKind.Number, "speechSettings.briefing.regionFacts.longest", Suffix = "min", Min = "1", Max = "480", DependsOn = "Enabled")]
    public int MaxIntervalMinutes { get; set; } = 30;

    /// <summary>Facts per flight at most; the counter resets with the cabin's own once-per-
    /// flight latches (cold-and-dark, or a fresh Preflight after shutdown).</summary>
    [OptionField(FieldKind.Number, "speechSettings.briefing.regionFacts.perFlight", Min = "1", Max = "20", DependsOn = "Enabled")]
    public int MaxPerFlight { get; set; } = 4;

    /// <summary>Ask the language model for the fact when it is configured and healthy
    /// (two sentences, aviation-passenger tone, same guard as the place facts). Off, or with
    /// the model down: the curated file only.</summary>
    [OptionField(FieldKind.Bool, "speechSettings.briefing.regionFacts.llmFacts", DependsOn = "Enabled")]
    public bool LlmFacts { get; set; } = true;
}
