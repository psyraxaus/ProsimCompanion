using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// Keeps config/settings.json complete and discoverable: every configurable option appears in
/// the file with its default value. Missing keys (new options added by an upgrade, or a
/// hand-trimmed file) are filled in at startup; existing values are never touched. The section
/// list is the <see cref="OptionSectionRegistry"/> — a section that binds is a section that
/// self-documents here, with no second list to keep in step (campaign #84).
/// </summary>
public static class SettingsDefaultsWriter
{
    /// <summary>Adds any missing option keys with their defaults. Returns true when the file was
    /// updated.</summary>
    public static bool EnsureDefaults(JsonSettingsFile file, OptionSectionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(registry);

        var current = file.Read();
        var missing = new List<(string Section, string Key, JsonNode? Value)>();

        foreach (var section in registry.Sections)
        {
            if (JsonSerializer.SerializeToNode(section.CreateDefaults(), SettingsJson.FileOptions)
                is not JsonObject defaultsNode)
            {
                continue;
            }

            var existing = current[section.SectionName] as JsonObject;
            foreach (var (key, value) in defaultsNode)
            {
                if (existing is null || !existing.ContainsKey(key))
                {
                    missing.Add((section.SectionName, key, value?.DeepClone()));
                }
            }
        }

        if (missing.Count == 0)
        {
            return false;
        }

        file.Update(root =>
        {
            foreach (var (section, key, value) in missing)
            {
                JsonSettingsFile.GetOrCreateSection(root, section)[key] = value;
            }
        });
        return true;
    }
}
