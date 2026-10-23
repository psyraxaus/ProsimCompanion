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

    /// <summary>A drill whose memory items are paced by a service on the flight data instead
    /// of read straight through (issue #187: <c>"efato"</c> — rotate at VR, V2, gear up on
    /// positive climb, ECAM hand-over at 400 ft). The monitor announces and raises
    /// <c>FailureDetected</c>; the named sequencer does the rest. A voice rehearsal still reads
    /// the <c>actions</c> straight through. Documented in docs/integrations/abnormals.md.</summary>
    public string? Sequencer { get; set; }

    public bool IsDrill => string.Equals(Class, "memoryDrill", StringComparison.OrdinalIgnoreCase);

    public bool IsSequenced => !string.IsNullOrWhiteSpace(Sequencer);
}

/// <summary>
/// The takeoff window a trigger must fall in (issue #187) — what the dataref condition
/// language cannot say: "after the V1 callout" is a comparison of two live figures (IAS
/// against the FMS V1) or being airborne already, and "below 400 ft AAL" is the AGL figure.
/// Evaluated from the engine's flight snapshot by the monitor; a window with no field set
/// is always open. Documented in docs/integrations/abnormals.md.
/// </summary>
public sealed class TriggerWindow
{
    /// <summary>IAS at or above the FMS V1 (V1 entered), or already off the ground.</summary>
    public bool AfterV1 { get; set; }

    /// <summary>Height above ground must be below this many feet (on the ground counts).</summary>
    public double? MaxAglFt { get; set; }
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

    /// <summary>Hold the trigger while GSX de-icing is in progress (crew Requested → Completed,
    /// from the Core de-icing activity seam). Shipped on the pack-fault definitions (issue
    /// #123 reopened, EFHK 2026-10-10): the crew switches the packs OFF for the treatment, and
    /// the FO read the resulting PACK FAULT lines as an abnormal.</summary>
    public bool HoldDuringDeice { get; set; }

    /// <summary>The takeoff window the trigger must fall in; null = no window (issue #187).</summary>
    public TriggerWindow? Window { get; set; }
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
