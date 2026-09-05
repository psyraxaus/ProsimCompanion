using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProsimCompanion.Core.Checklists;

/// <summary>
/// Checklist definition files — deliberately file-compatible with the Prosim2FO voice
/// checklists (same camelCase/tolerant serializer contract), so a user's edited Prosim2FO
/// checklist drops straight in. Voice-only fields (acceptedPhrases, callouts, action sweeps)
/// deserialize and are ignored by the visual engine; the one visual-only addition is the
/// per-item <c>freeze</c> flag.
/// </summary>
public sealed class ChecklistDefinition
{
    /// <summary>Shared wire contract: camelCase, case-insensitive, comments + trailing commas
    /// tolerated, enums as camelCase strings (identical to the predecessor's options).</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Display name; a file without one is skipped at load.</summary>
    public string Checklist { get; set; } = "";

    /// <summary>Ascending display/next-checklist order; absent sorts last.</summary>
    public int? Order { get; set; }

    /// <summary>Voice trigger phrases; absent defaults to "{name} checklist".</summary>
    public List<string>? StartPhrases { get; set; }

    public List<ChecklistItemDefinition> Items { get; set; } = [];
}

public sealed class ChecklistItemDefinition
{
    /// <summary>Prosim2FO behaviors: verify | acknowledge | action | monitorControls |
    /// captureMinima. The visual engine only distinguishes "has a verify condition" (auto)
    /// from everything else (manual tick) — kept as a string so foreign values degrade to
    /// manual instead of failing deserialization.</summary>
    public string Behavior { get; set; } = "acknowledge";

    /// <summary>The challenge text — the row label.</summary>
    public string Say { get; set; } = "";

    /// <summary>The response column ("ON", "SET", …).</summary>
    public string? ExpectedResponse { get; set; }

    public VerifyCondition? Verify { get; set; }

    /// <summary>Visual-engine addition: a completed auto item normally RETREATS (un-checks)
    /// if its condition regresses before the checklist completes; freeze latches it Done
    /// regardless. Manual items always freeze.</summary>
    public bool Freeze { get; set; }

    /// <summary>Display kind: "normal" (default) | "separator" | "note" — the Prosim2GSX set
    /// format's display-only rows. The runner completes separator/note rows instantly so they
    /// never gate the checklist. Kept as a string so foreign values degrade to normal instead
    /// of failing deserialization (same rationale as <see cref="Behavior"/>).</summary>
    public string Kind { get; set; } = "normal";

    /// <summary>True for separator/note rows — display furniture, never a gate.</summary>
    [JsonIgnore]
    public bool IsDisplayOnly =>
        string.Equals(Kind, "separator", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Kind, "note", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the item completes itself from a dataref condition.</summary>
    [JsonIgnore]
    public bool IsAuto => Verify is not null;

    // ---- Voice-engine fields (Prosim2FO schema; ignored by the visual engine) ----

    /// <summary>Pilot replies that satisfy this item (exact/whole-word matched; NOT the
    /// display-only <see cref="ExpectedResponse"/>).</summary>
    public List<string> AcceptedPhrases { get; set; } = [];

    /// <summary>What the FO says after the item completes.</summary>
    public string? ConfirmCallout { get; set; }

    /// <summary>Verify-mismatch retries before the "still not set" escape line (default 3).</summary>
    public int? MaxRetries { get; set; }

    /// <summary>"none" | "number" | "flapConfig" — number items accept any spoken number;
    /// flapConfig items (issue #125) parse an Airbus takeoff config ("config 1 plus F") and
    /// read it back against the flap lever and the FMS PERF TO entry.</summary>
    public string Expects { get; set; } = "none";

    /// <summary>When set with Expects=number: the spoken number is compared to this dataref
    /// within <see cref="ReadbackTolerance"/> (default 0.5).</summary>
    public string? ReadbackDataref { get; set; }
    public double? ReadbackTolerance { get; set; }

    // ---- monitorControls fields ----

    /// <summary>"monitorOnly" | "sweepOnly" | "monitorThenSweep" (default).</summary>
    public string? Composition { get; set; }

    /// <summary>"reactive" (any order, default) | "sequenced".</summary>
    public string? Mode { get; set; }

    public int? DwellMs { get; set; }
    public double? FullThreshold { get; set; }
    public double? NeutralThreshold { get; set; }

    /// <summary>Captain-side axes to monitor, keyed "pitch"/"roll"/"rudder".</summary>
    public Dictionary<string, ControlAxisDefinition>? Axes { get; set; }

    /// <summary>The FO's own sweep sequence (writes to the FO-side analog datarefs only).</summary>
    public ControlActionDefinition? Action { get; set; }
}

/// <summary>One monitored captain-control axis (read-only datarefs).</summary>
public sealed class ControlAxisDefinition
{
    public string Dataref { get; set; } = "";

    /// <summary>Which physical direction the positive sign means ("down", "right") — doc
    /// only; the callouts below carry the actual wording.</summary>
    public string? Positive { get; set; }

    [JsonPropertyName("full+")]
    public string FullPositive { get; set; } = "";

    [JsonPropertyName("full-")]
    public string FullNegative { get; set; } = "";

    public string Neutral { get; set; } = "Neutral";
}

/// <summary>A scripted control sweep (the FO's flight-control check).</summary>
public sealed class ControlActionDefinition
{
    public List<ControlSweepStep> Steps { get; set; } = [];
}

/// <summary>One ramp-to-target step. Normalized −1..0..+1 maps to ProSim analog 0..512..1024
/// at write time.</summary>
public sealed class ControlSweepStep
{
    public string Dataref { get; set; } = "";
    public double To { get; set; }
    public int RampMs { get; set; } = 700;
    public int HoldMs { get; set; }
}

public enum ComparisonOp
{
    Equals,
    NotEquals,
    GreaterThan,
    LessThan,
    Between,
    OneOf,
}

public enum ConditionLogic
{
    And,
    Or,
}

/// <summary>A leaf (dataref + op) or a compound node (logic + children), nesting freely —
/// the Prosim2FO condition tree, verbatim schema.</summary>
public sealed class VerifyCondition
{
    public string? Dataref { get; set; }
    public ComparisonOp? Op { get; set; }
    public double? Value { get; set; }
    public double? Low { get; set; }
    public double? High { get; set; }
    public List<double>? Values { get; set; }
    public ConditionLogic? Logic { get; set; }
    public List<VerifyCondition>? Conditions { get; set; }

    [JsonIgnore]
    public bool IsCompound => Conditions is { Count: > 0 };

    /// <summary>Every dataref referenced anywhere in this tree (for subscription setup).</summary>
    public IEnumerable<string> ReferencedDatarefs()
    {
        if (IsCompound)
        {
            foreach (var dataref in Conditions!.SelectMany(child => child.ReferencedDatarefs()))
            {
                yield return dataref;
            }
        }
        else if (!string.IsNullOrWhiteSpace(Dataref))
        {
            yield return Dataref;
        }
    }
}

public enum ChecklistItemStatus
{
    Pending,
    Active,
    Done,
    Skipped,
}

public sealed record ChecklistItemView(
    string Label,
    string Response,
    ChecklistItemStatus Status,
    bool IsAuto,
    bool ConditionSatisfied,
    string Kind = "normal");

public sealed record ChecklistView(
    string Name,
    bool IsComplete,
    int ActiveIndex,
    IReadOnlyList<ChecklistItemView> Items);

public sealed record ChecklistCatalogEntry(string Name, int Order, int ItemCount);

/// <summary>A named collection of checklists shown as one entry in the web page's set
/// dropdown: the shipped per-phase folder is one set; every Prosim2GSX-format file under
/// <c>config/checklists/sets</c> is another. Definitions are not mutated after load.</summary>
public sealed class ChecklistSet
{
    public ChecklistSet(string name, List<ChecklistDefinition> definitions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(definitions);
        Name = name;
        Definitions = definitions;
    }

    public string Name { get; }

    public List<ChecklistDefinition> Definitions { get; }
}
