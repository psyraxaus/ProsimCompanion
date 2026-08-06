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

    /// <summary>True when the item completes itself from a dataref condition.</summary>
    [JsonIgnore]
    public bool IsAuto => Verify is not null;
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
    bool ConditionSatisfied);

public sealed record ChecklistView(
    string Name,
    bool IsComplete,
    int ActiveIndex,
    IReadOnlyList<ChecklistItemView> Items);

public sealed record ChecklistCatalogEntry(string Name, int Order, int ItemCount);
