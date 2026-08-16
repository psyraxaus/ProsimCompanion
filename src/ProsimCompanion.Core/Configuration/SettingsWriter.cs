using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// The typed settings write path (campaign #84): section and camelCase JSON keys are derived
/// from options types and property expressions, never hand-written — a property rename breaks
/// the build, not the runtime. All writes go through <see cref="JsonSettingsFile"/>, so
/// unrelated file content is preserved.
/// </summary>
public sealed class SettingsWriter
{
    private readonly JsonSettingsFile _file;

    public SettingsWriter(JsonSettingsFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        _file = file;
    }

    /// <summary>
    /// Writes one option value immediately (the no-dirty-bar pages: Logs, OFP Korry buttons).
    /// The selector must be a property chain rooted at the options parameter — nested objects
    /// become nested JSON keys (<c>o =&gt; o.PttBinding.Key</c> → <c>speech.pttBinding.key</c>).
    /// </summary>
    public void Set<TOptions, TValue>(Expression<Func<TOptions, TValue>> property, TValue value)
        where TOptions : IOptionSection
    {
        var path = CamelCasePath(property);
        _file.Update(root =>
        {
            var node = JsonSettingsFile.GetOrCreateSection(root, TOptions.SectionName);
            for (var i = 0; i < path.Count - 1; i++)
            {
                node = JsonSettingsFile.GetOrCreateSection(node, path[i]);
            }

            node[path[^1]] = value is null
                ? null
                : JsonSerializer.SerializeToNode(value, SettingsJson.DraftOptions);
        });
    }

    /// <summary>
    /// Persists every dirty draft in one atomic file write, then re-baselines them. Only
    /// top-level keys whose serialized value changed are written; a clean set of drafts is a
    /// no-op returning false.
    /// </summary>
    public bool Save(params IReadOnlyList<OptionsDraft> drafts)
    {
        ArgumentNullException.ThrowIfNull(drafts);

        var pending = new List<(string Section, string Key, JsonNode? Value)>();
        var serialized = new Dictionary<OptionsDraft, JsonObject>();
        foreach (var draft in drafts)
        {
            var current = draft.SerializeDraft();
            serialized[draft] = current;
            // DraftOptions keeps nulls, so baseline and draft always carry the same key set —
            // a value cleared to null still shows up as a change and writes an explicit null.
            foreach (var (key, value) in current)
            {
                if (!JsonNode.DeepEquals(value, draft.Baseline[key]))
                {
                    pending.Add((draft.Section, key, value?.DeepClone()));
                }
            }
        }

        if (pending.Count == 0)
        {
            return false;
        }

        _file.Update(root =>
        {
            foreach (var (section, key, value) in pending)
            {
                JsonSettingsFile.GetOrCreateSection(root, section)[key] = value;
            }
        });

        foreach (var draft in drafts)
        {
            draft.Rebaseline(serialized[draft]);
        }

        return true;
    }

    private static List<string> CamelCasePath(LambdaExpression property)
    {
        var segments = new List<string>();
        var expression = property.Body;
        while (expression is MemberExpression { Member: PropertyInfo memberProperty } member)
        {
            segments.Insert(0, JsonNamingPolicy.CamelCase.ConvertName(memberProperty.Name));
            expression = member.Expression!;
        }

        if (expression is not ParameterExpression || segments.Count == 0)
        {
            throw new ArgumentException(
                "The selector must be a property chain on the options parameter, "
                + "e.g. o => o.PushbackPreference or o => o.PttBinding.Key.",
                nameof(property));
        }

        return segments;
    }
}
