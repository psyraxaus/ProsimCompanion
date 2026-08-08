using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ProsimCompanion.Core.Checklists;

/// <summary>
/// Parses a Prosim2GSX checklist file (the a320_default.json shape: a Name plus Sections[],
/// each item carrying condition STRINGS like <c>"== 1"</c> / <c>"&lt;= 0.05"</c> /
/// <c>"== true"</c>) into a <see cref="ChecklistSet"/> — one <see cref="ChecklistDefinition"/>
/// per section, in file order. The files are kept in their ORIGINAL Prosim2GSX shape on disk so
/// a Prosim2GSX user's own checklist drops straight into <c>config/checklists/sets</c>.
///
/// Condition mapping onto the <see cref="VerifyCondition"/> tree:
/// <list type="bullet">
/// <item><c>==</c>/<c>!=</c>/<c>&gt;</c>/<c>&lt;</c> → Equals/NotEquals/GreaterThan/LessThan.</item>
/// <item><c>&gt;=</c>/<c>&lt;=</c> → inclusive one-sided Between (our op set has no OrEqual
/// relationals; Between's open-ended defaults make it an exact representation).</item>
/// <item><c>true</c>/<c>false</c> operands → 1/0 (booleans read 1/0 in our evaluator), valid
/// only with ==/!= — exactly the predecessor's rule.</item>
/// <item><c>DataRefs[]</c> compound items → an AND tree, all-leaves-or-fallback.</item>
/// <item><c>SteadyDataRef</c> (momentary-switch steady read) replaces the documented DataRef
/// as the evaluated leaf — same precedence as the predecessor's worker.</item>
/// </list>
/// Anything unrepresentable (missing operator, unparseable operand, boolean with a relational
/// op) falls back to a manual acknowledge item with a logged warning — degrade, not fail.
/// </summary>
public static class Prosim2GsxChecklistSetLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Parses one Prosim2GSX-format file. Returns null when the JSON is not a usable
    /// set (no sections); malformed JSON throws <see cref="JsonException"/> — the caller owns
    /// per-file error handling, mirroring the per-phase loader.</summary>
    /// <param name="json">Raw file text in the native Prosim2GSX shape.</param>
    /// <param name="fallbackName">Set name when the file has no <c>Name</c> (the filename).</param>
    /// <param name="logger">Sink for per-item fallback warnings.</param>
    public static ChecklistSet? Parse(string json, string fallbackName, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackName);
        ArgumentNullException.ThrowIfNull(logger);

        var file = JsonSerializer.Deserialize<P2GsxFile>(json, JsonOptions);
        if (file?.Sections is not { Count: > 0 })
        {
            return null;
        }

        var setName = string.IsNullOrWhiteSpace(file.Name) ? fallbackName : file.Name.Trim();
        var definitions = new List<ChecklistDefinition>();
        for (var i = 0; i < file.Sections.Count; i++)
        {
            var section = file.Sections[i];
            var title = string.IsNullOrWhiteSpace(section.Title) ? $"Section {i + 1}" : section.Title.Trim();
            if (definitions.Any(existing => string.Equals(existing.Checklist, title, StringComparison.OrdinalIgnoreCase)))
            {
                logger.LogWarning("Checklist set {Set}: duplicate section title '{Title}' — first wins", setName, title);
                continue;
            }
            definitions.Add(new ChecklistDefinition
            {
                Checklist = title,
                Order = i + 1, // file order IS the display/next-checklist order
                Items = [.. (section.Items ?? []).Select(item => MapItem(item, setName, logger))],
            });
        }

        return new ChecklistSet(setName, definitions);
    }

    private static ChecklistItemDefinition MapItem(P2GsxItem item, string setName, ILogger logger)
    {
        var mapped = new ChecklistItemDefinition
        {
            Say = item.Label ?? "",
            ExpectedResponse = item.Value,
        };

        if (item.IsSeparator)
        {
            mapped.Kind = "separator";
            return mapped;
        }
        if (item.IsNote)
        {
            mapped.Kind = "note";
            return mapped;
        }

        var verify = MapVerify(item, setName, logger);
        if (verify is not null)
        {
            mapped.Behavior = "verify";
            mapped.Verify = verify;
        }
        return mapped; // no condition → the default acknowledge (manual tick)
    }

    /// <summary>Builds the condition tree, or null for manual items and unmappable conditions
    /// (fallback already logged). Compound items are all-or-nothing: dropping one AND leaf
    /// would let the item auto-complete on a partial truth, so an unmappable leaf demotes the
    /// whole item to manual.</summary>
    private static VerifyCondition? MapVerify(P2GsxItem item, string setName, ILogger logger)
    {
        if (item.DataRefs is { Count: > 0 })
        {
            var leaves = new List<VerifyCondition>();
            foreach (var compound in item.DataRefs)
            {
                var dataref = FirstNonEmpty(compound?.SteadyDataRef, compound?.DataRef);
                if (compound is null || dataref is null || string.IsNullOrWhiteSpace(compound.Condition))
                {
                    continue; // the predecessor skips incomplete entries too
                }
                var leaf = MapLeaf(dataref, compound.Condition);
                if (leaf is null)
                {
                    logger.LogWarning(
                        "Checklist set {Set}: item '{Label}' condition '{Condition}' on {Dataref} not representable — item falls back to manual",
                        setName, item.Label, compound.Condition, dataref);
                    return null;
                }
                leaves.Add(leaf);
            }
            return leaves.Count switch
            {
                0 => null,
                1 => leaves[0],
                _ => new VerifyCondition { Logic = ConditionLogic.And, Conditions = leaves },
            };
        }

        var primary = FirstNonEmpty(item.SteadyDataRef, item.DataRef);
        if (primary is null || string.IsNullOrWhiteSpace(item.DataRefCondition))
        {
            return null; // DataRef: null is the P2GSX idiom for a manual item
        }
        var single = MapLeaf(primary, item.DataRefCondition);
        if (single is null)
        {
            logger.LogWarning(
                "Checklist set {Set}: item '{Label}' condition '{Condition}' on {Dataref} not representable — item falls back to manual",
                setName, item.Label, item.DataRefCondition, primary);
        }
        return single;
    }

    /// <summary>Maps one "&lt;op&gt; &lt;operand&gt;" condition string to a leaf, or null when
    /// it cannot be represented.</summary>
    private static VerifyCondition? MapLeaf(string dataref, string condition)
    {
        var raw = condition.Trim();
        string op;
        string operand;
        // Two-char operators must be checked first (predecessor parser, verbatim).
        if (raw.StartsWith("==", StringComparison.Ordinal)) { op = "=="; operand = raw[2..].Trim(); }
        else if (raw.StartsWith("!=", StringComparison.Ordinal)) { op = "!="; operand = raw[2..].Trim(); }
        else if (raw.StartsWith(">=", StringComparison.Ordinal)) { op = ">="; operand = raw[2..].Trim(); }
        else if (raw.StartsWith("<=", StringComparison.Ordinal)) { op = "<="; operand = raw[2..].Trim(); }
        else if (raw.StartsWith('>')) { op = ">"; operand = raw[1..].Trim(); }
        else if (raw.StartsWith('<')) { op = "<"; operand = raw[1..].Trim(); }
        else { return null; }

        double target;
        if (operand.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            if (op is not ("==" or "!=")) { return null; } // booleans only compare for (in)equality
            target = 1;
        }
        else if (operand.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            if (op is not ("==" or "!=")) { return null; }
            target = 0;
        }
        else if (!double.TryParse(operand, NumberStyles.Float, CultureInfo.InvariantCulture, out target))
        {
            return null;
        }

        return op switch
        {
            "==" => new VerifyCondition { Dataref = dataref, Op = ComparisonOp.Equals, Value = target },
            "!=" => new VerifyCondition { Dataref = dataref, Op = ComparisonOp.NotEquals, Value = target },
            ">" => new VerifyCondition { Dataref = dataref, Op = ComparisonOp.GreaterThan, Value = target },
            "<" => new VerifyCondition { Dataref = dataref, Op = ComparisonOp.LessThan, Value = target },
            // Our op set has no OrEqual relationals; Between is inclusive with open-ended
            // defaults, so a one-sided Between is an exact >= / <=.
            ">=" => new VerifyCondition { Dataref = dataref, Op = ComparisonOp.Between, Low = target },
            "<=" => new VerifyCondition { Dataref = dataref, Op = ComparisonOp.Between, High = target },
            _ => null,
        };
    }

    private static string? FirstNonEmpty(string? preferred, string? fallback)
        => !string.IsNullOrWhiteSpace(preferred) ? preferred
            : !string.IsNullOrWhiteSpace(fallback) ? fallback : null;

    // ---- Native Prosim2GSX wire shapes (PascalCase on disk; matched case-insensitively) ----

    private sealed class P2GsxFile
    {
        public string? Name { get; set; }
        public List<P2GsxSection>? Sections { get; set; }
    }

    private sealed class P2GsxSection
    {
        public string? Title { get; set; }
        public List<P2GsxItem>? Items { get; set; }
    }

    private sealed class P2GsxItem
    {
        public string? Label { get; set; }
        public string? Value { get; set; }
        public string? DataRef { get; set; }
        public string? DataRefCondition { get; set; }

        /// <summary>Steady-state read for momentary switches: evaluated INSTEAD of DataRef
        /// (which then only documents intent) — the predecessor's precedence rule.</summary>
        public string? SteadyDataRef { get; set; }

        public List<P2GsxCompoundCondition>? DataRefs { get; set; }
        public bool IsNote { get; set; }
        public bool IsSeparator { get; set; }
    }

    private sealed class P2GsxCompoundCondition
    {
        public string? DataRef { get; set; }
        public string? Condition { get; set; }
        public string? SteadyDataRef { get; set; }
    }
}
