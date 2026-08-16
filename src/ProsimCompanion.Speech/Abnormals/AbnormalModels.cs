using System.Text.Json;
using ProsimCompanion.Core.Checklists;

namespace ProsimCompanion.Speech.Abnormals;

/// <summary>One abnormal procedure or memory drill — file-compatible with Prosim2FO's
/// abnormals/*.json (the shipped 30 are carried verbatim). This pillar is detect-and-report
/// only: it never actuates a switch and never injects a failure. Detection announces; drills
/// speak their rapid memory items straight through; an ECAM procedure with action lines then
/// runs the interactive per-line dialogue (<see cref="EcamDialogueCore"/>), each line gated on
/// the pilot's confirm phrases.</summary>
public sealed class AbnormalDefinition
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";

    /// <summary>"memoryDrill" marks a drill; anything else is an ECAM procedure.</summary>
    public string? Class { get; set; }

    /// <summary>"warning" (Critical speech) or "caution" (High). Drills always Critical.</summary>
    public string Severity { get; set; } = "caution";

    public bool Enabled { get; set; } = true;

    /// <summary>Arming phases (FlightPhase names); empty = any.</summary>
    public List<string> Phases { get; set; } = [];

    public List<string> VoiceTriggers { get; set; } = [];

    public AbnormalTrigger? Trigger { get; set; }

    public string Announce { get; set; } = "";

    public List<AbnormalAction> Actions { get; set; } = [];

    public List<string> Status { get; set; } = [];

    public List<string> ContextFlags { get; set; } = [];

    public VerifyCondition? ClearedWhen { get; set; }

    public bool IsDrill => string.Equals(Class, "memoryDrill", StringComparison.OrdinalIgnoreCase);
}

public sealed class AbnormalTrigger
{
    /// <summary>E/WD text substrings (case-insensitive) — primary signal.</summary>
    public List<string> EwdText { get; set; } = [];

    /// <summary>Per-system dataref condition — corroborating or fallback signal.</summary>
    public VerifyCondition? Condition { get; set; }

    /// <summary>"any" (default) or "all" when both text and condition are present.</summary>
    public string Logic { get; set; } = "any";

    /// <summary>Optional master/ECAM-page light that must read &gt; 0.5.</summary>
    public string? Corroborate { get; set; }

    /// <summary>Rising edge must hold this long (clamped ≥ 0.5 s at load).</summary>
    public double DebounceSeconds { get; set; } = 2.0;
}

public sealed class AbnormalAction
{
    public string? Kind { get; set; }
    public string Say { get; set; } = "";
    public List<string> Confirm { get; set; } = [];
    public VerifyCondition? Verify { get; set; }
    public VerifyCondition? Condition { get; set; }
    public string? Ack { get; set; }
    public string? Discrepancy { get; set; }
}

/// <summary>Loads abnormals/*.json once at startup (hot reload can come later).</summary>
public static class AbnormalLoader
{
    /// <summary>
    /// Loads every definition in <paramref name="folder"/>. A malformed or id-less file skips
    /// itself (the loader never fails the pillar) and is reported through
    /// <paramref name="onProblem"/> (file, message) so the caller can log it and surface it on
    /// the web UI (issue #74 — silent skips left pilots flying without their edits).
    /// </summary>
    public static IReadOnlyList<AbnormalDefinition> LoadFolder(
        string folder, Action<string, string>? onProblem = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        if (!Directory.Exists(folder))
        {
            return [];
        }

        var definitions = new List<AbnormalDefinition>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
        {
            try
            {
                var definition = JsonSerializer.Deserialize<AbnormalDefinition>(
                    File.ReadAllText(file), ChecklistDefinition.JsonOptions);
                if (definition is { Id.Length: > 0 })
                {
                    definition.Trigger ??= new AbnormalTrigger();
                    definition.Trigger.DebounceSeconds = Math.Max(0.5, definition.Trigger.DebounceSeconds);
                    definitions.Add(definition);
                }
                else
                {
                    onProblem?.Invoke(file, "no \"id\" — definition skipped");
                }
            }
            catch (Exception ex)
            {
                onProblem?.Invoke(file, ex.Message);
            }
        }

        return definitions;
    }
}
