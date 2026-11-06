using System.Text.Json;
using ProsimCompanion.Core.TechLog;

namespace ProsimCompanion.Core.Crew;

/// <summary>How bad a walkaround finding is (issue #196).</summary>
public enum WalkaroundSeverity
{
    /// <summary>The FO deals with it on the spot; no tech-log item.</summary>
    Cosmetic,

    /// <summary>Worth a tech-log entry; the captain is asked.</summary>
    Deferrable,

    /// <summary>Raised regardless; only drawn when <c>walkaround.allowNoGo</c> is on.</summary>
    NoGo,
}

/// <summary>The conditions a finding needs (all listed must hold); an empty set always applies.</summary>
public sealed record WalkaroundConditions(bool Icing, bool Precipitation, bool Night, bool AfterLanding)
{
    public static WalkaroundConditions None { get; } = new(false, false, false, false);

    /// <summary>What is true right now at the stand.</summary>
    public bool SatisfiedBy(WalkaroundConditions now)
    {
        ArgumentNullException.ThrowIfNull(now);
        return (!Icing || now.Icing) && (!Precipitation || now.Precipitation) && (!Night || now.Night) && (!AfterLanding || now.AfterLanding);
    }
}

/// <summary>One entry of <c>walkaround-findings.json</c>.</summary>
/// <param name="Spoken">What the FO says in the report, after "Walkaround complete, captain."</param>
/// <param name="TechLogTitle">The tech-log item title (deferrable / noGo); null = no item.</param>
/// <param name="TechLogCategory">The MEL category the item is raised under.</param>
/// <param name="Weight">Relative draw weight among the findings whose conditions hold.</param>
/// <param name="Deice">True for the icing finding: it leads into the de-ice question, never a tech-log item.</param>
public sealed record WalkaroundFinding(
    string Id,
    string Spoken,
    WalkaroundSeverity Severity,
    string? TechLogTitle,
    MelCategory TechLogCategory,
    WalkaroundConditions Conditions,
    double Weight,
    bool Deice = false);

/// <summary>Pure parsing of <c>walkaround-findings.json</c>: an object with a <c>findings</c>
/// array (or a bare array). Severity and category are normalised; an entry without an id or
/// spoken text is skipped and named in the problems.</summary>
public static class WalkaroundFindingsFile
{
    public sealed record ParseResult(IReadOnlyList<WalkaroundFinding> Findings, IReadOnlyList<string> Problems);

    public static ParseResult Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = document.RootElement;
        JsonElement array;
        if (root.ValueKind == JsonValueKind.Array)
        {
            array = root;
        }
        else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("findings", out var findings) && findings.ValueKind == JsonValueKind.Array)
        {
            array = findings;
        }
        else
        {
            return new([], ["no \"findings\" array"]);
        }

        var entries = new List<WalkaroundFinding>();
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var item in array.EnumerateArray())
        {
            index++;
            if (item.ValueKind != JsonValueKind.Object)
            {
                problems.Add($"entry {index}: not an object");
                continue;
            }

            var id = Text(item, "id");
            var spoken = Text(item, "spoken");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(spoken))
            {
                problems.Add($"entry {index}: id and spoken are required");
                continue;
            }

            if (!seen.Add(id))
            {
                problems.Add($"entry {index}: duplicate id \"{id}\"");
                continue;
            }

            var severity = (Text(item, "severity") ?? "").Trim().ToLowerInvariant() switch
            {
                "deferrable" => WalkaroundSeverity.Deferrable,
                "nogo" => WalkaroundSeverity.NoGo,
                _ => WalkaroundSeverity.Cosmetic,
            };
            var category = (Text(item, "techLogCategory") ?? "C").Trim().ToUpperInvariant() switch
            {
                "A" => MelCategory.A,
                "B" => MelCategory.B,
                "D" => MelCategory.D,
                _ => MelCategory.C,
            };
            var conditions = WalkaroundConditions.None;
            if (item.TryGetProperty("conditions", out var cond) && cond.ValueKind == JsonValueKind.Array)
            {
                var words = cond.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.String).Select(c => c.GetString()!.Trim().ToLowerInvariant()).ToHashSet();
                conditions = new WalkaroundConditions(words.Contains("icing"), words.Contains("precipitation"), words.Contains("night"), words.Contains("afterlanding"));
            }

            var weight = item.TryGetProperty("weight", out var w) && w.ValueKind == JsonValueKind.Number && w.TryGetDouble(out var wd) ? Math.Max(0, wd) : 1.0;
            var deice = item.TryGetProperty("deice", out var d) && d.ValueKind == JsonValueKind.True;
            var title = Text(item, "techLogTitle");
            entries.Add(new WalkaroundFinding(
                id.Trim(), spoken.Trim(), severity,
                string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
                category, conditions, weight, deice));
        }

        return new(entries, problems);
    }

    private static string? Text(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>
/// The pure draw (issue #196): at most one finding per walkaround, with the configured
/// probability, weighted among the findings whose conditions hold right now; noGo findings
/// are excluded unless allowed. Two dice are passed in: the chance roll and the weight roll.
/// </summary>
public static class WalkaroundFindingDraw
{
    /// <summary>One finding or none: the first roll against <paramref name="findingChance"/>
    /// decides whether anything is found, the second picks by weight among the findings whose
    /// conditions hold now (no-go ones only with <paramref name="allowNoGo"/>).</summary>
    public static WalkaroundFinding? Draw(
        IReadOnlyList<WalkaroundFinding> bank,
        WalkaroundConditions now,
        double findingChance,
        bool allowNoGo,
        Func<double> roll)
    {
        ArgumentNullException.ThrowIfNull(bank);
        ArgumentNullException.ThrowIfNull(now);
        ArgumentNullException.ThrowIfNull(roll);

        if (findingChance <= 0 || roll() >= Math.Clamp(findingChance, 0, 1))
        {
            return null;
        }

        var candidates = bank
            .Where(f => f.Weight > 0 && (allowNoGo || f.Severity != WalkaroundSeverity.NoGo) && f.Conditions.SatisfiedBy(now))
            .ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        var total = candidates.Sum(f => f.Weight);
        var point = Math.Clamp(roll(), 0, 0.999999) * total;
        foreach (var candidate in candidates)
        {
            point -= candidate.Weight;
            if (point < 0)
            {
                return candidate;
            }
        }

        return candidates[^1];
    }
}
