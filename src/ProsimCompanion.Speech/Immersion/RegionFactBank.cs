using System.Text.Json;
using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Speech.Immersion;

/// <summary>
/// The curated offline facts (issue #122), file-backed by <c>region-facts.json</c> in the USER
/// config tree (ADR-0007: seeded from <c>{app}\config</c>, user edits kept) on the
/// <see cref="UserConfigFileBank{T}"/> contract. Format: a flat object of string arrays keyed by
/// the region key the atlas yields — the ISO 3166-1 alpha-2 country code ("FR", "GB") or
/// <c>sea:&lt;name&gt;</c> for open water ("sea:North Sea"). Keys starting with "_" are
/// comments. A missing or unparseable file means no curated tier: the model tier (when up)
/// still speaks, and the problem is reported on the web UI once.
/// </summary>
public sealed class RegionFactBank : UserConfigFileBank<Dictionary<string, string[]>>
{
    public const string FileName = "region-facts.json";

    private readonly ILogger<RegionFactBank> _logger;

    public RegionFactBank(string configDirectory, ILogger<RegionFactBank> logger, Core.State.ConfigProblemStore? problems = null)
        : base(Path.Combine(configDirectory ?? throw new ArgumentNullException(nameof(configDirectory)), FileName), Core.State.ConfigAreas.RegionFacts, logger, problems, Empty())
    {
        _logger = logger;
    }

    /// <summary>The facts on file for a region key; empty when there are none.</summary>
    public IReadOnlyList<string> FactsFor(string regionKey)
    {
        if (string.IsNullOrWhiteSpace(regionKey))
        {
            return [];
        }

        return Current.TryGetValue(regionKey, out var facts) ? facts : [];
    }

    /// <summary>How many regions the file covers (for the log line and the settings hint).</summary>
    public int RegionCount => Current.Count;

    protected override (Dictionary<string, string[]> Value, IReadOnlyList<string> Problems) Parse(string json)
    {
        using var document = JsonDocument.Parse(json, JsonConfigText.DocumentOptions);
        return (Parse(document.RootElement), []);
    }

    /// <summary>An unreadable file yields no curated facts — not the last good set.</summary>
    protected override Dictionary<string, string[]> OnUnreadable(Dictionary<string, string[]> lastGood) => Empty();

    protected override void OnLoaded(Dictionary<string, string[]> value)
        => _logger.LogInformation("{File} loaded: {Regions} regions, {Facts} facts", FileName, value.Count, value.Values.Sum(f => f.Length));

    private static Dictionary<string, string[]> Empty() => new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The flat-object parse, public for the tests: every string-array property
    /// whose name does not start with "_", blank entries dropped.</summary>
    public static Dictionary<string, string[]> Parse(JsonElement root)
    {
        var facts = Empty();
        if (root.ValueKind != JsonValueKind.Object)
        {
            return facts;
        }

        foreach (var property in root.EnumerateObject())
        {
            if (property.Name.StartsWith('_') || property.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var list = property.Value.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!.Trim())
                .Where(text => text.Length > 0)
                .ToArray();
            if (list.Length > 0)
            {
                facts[property.Name.Trim()] = list;
            }
        }

        return facts;
    }
}
