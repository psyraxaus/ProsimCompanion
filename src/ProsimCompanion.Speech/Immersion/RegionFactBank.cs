using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ProsimCompanion.Speech.Immersion;

/// <summary>
/// The curated offline facts (issue #122), file-backed by <c>region-facts.json</c> in the USER
/// config tree (ADR-0007: seeded from <c>{app}\config</c>, user edits kept). Format: a flat
/// object of string arrays keyed by the region key the atlas yields — the ISO 3166-1 alpha-2
/// country code ("FR", "GB") or <c>sea:&lt;name&gt;</c> for open water ("sea:North Sea").
/// Keys starting with "_" are comments. A missing or unparseable file means no curated tier:
/// the model tier (when up) still speaks, and the problem is reported on the web UI once.
/// Edits are picked up on the next access via a last-write-time check, like phrases.json.
/// </summary>
public sealed class RegionFactBank
{
    public const string FileName = "region-facts.json";

    private readonly string _path;
    private readonly ILogger<RegionFactBank> _logger;
    private readonly Core.State.ConfigProblemStore? _problems;
    private readonly object _gate = new();
    private Dictionary<string, string[]> _facts = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _loadedWriteTimeUtc = DateTime.MinValue;

    public RegionFactBank(string configDirectory, ILogger<RegionFactBank> logger, Core.State.ConfigProblemStore? problems = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);
        ArgumentNullException.ThrowIfNull(logger);
        _path = Path.Combine(configDirectory, FileName);
        _logger = logger;
        _problems = problems;
    }

    /// <summary>The facts on file for a region key; empty when there are none.</summary>
    public IReadOnlyList<string> FactsFor(string regionKey)
    {
        if (string.IsNullOrWhiteSpace(regionKey))
        {
            return [];
        }

        lock (_gate)
        {
            ReloadIfChanged();
            return _facts.TryGetValue(regionKey, out var facts) ? facts : [];
        }
    }

    /// <summary>How many regions the file covers (for the log line and the settings hint).</summary>
    public int RegionCount
    {
        get
        {
            lock (_gate)
            {
                ReloadIfChanged();
                return _facts.Count;
            }
        }
    }

    private void ReloadIfChanged()
    {
        DateTime writeTime;
        try
        {
            writeTime = File.GetLastWriteTimeUtc(_path);
        }
        catch (IOException)
        {
            return;
        }

        if (writeTime == _loadedWriteTimeUtc)
        {
            return;
        }

        _loadedWriteTimeUtc = writeTime;
        if (!File.Exists(_path))
        {
            _facts = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            _problems?.ClearArea(Core.State.ConfigAreas.RegionFacts);
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(
                File.ReadAllText(_path),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            _facts = Parse(document.RootElement);
            _logger.LogInformation("{File} loaded: {Regions} regions, {Facts} facts", FileName, _facts.Count, _facts.Values.Sum(f => f.Length));
            _problems?.ClearArea(Core.State.ConfigAreas.RegionFacts);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogWarning(ex, "{File} unreadable — no curated region facts", FileName);
            _problems?.Report(Core.State.ConfigAreas.RegionFacts, _path, ex.Message);
            _facts = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>The flat-object parse, public for the tests: every string-array property
    /// whose name does not start with "_", blank entries dropped.</summary>
    public static Dictionary<string, string[]> Parse(JsonElement root)
    {
        var facts = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
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
