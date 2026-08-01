using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ProsimCompanion.Core.Profiles;

namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// Keeps config/settings.json complete and discoverable: every configurable option appears in
/// the file with its default value. Missing keys (new options added by an upgrade, or a
/// hand-trimmed file) are filled in at startup; existing values are never touched. Register new
/// options classes here so their settings self-document in the file.
/// </summary>
public static class SettingsDefaultsWriter
{
    private static readonly JsonSerializerOptions SerializeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Adds any missing option keys with their defaults. Returns true when the file was
    /// updated.</summary>
    public static bool EnsureDefaults(JsonSettingsFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        (string Name, object Defaults)[] sections =
        [
            (WebUiOptions.SectionName, new WebUiOptions()),
            (ProsimOptions.SectionName, new ProsimOptions()),
            (GsxOptions.SectionName, new GsxOptions()),
            (LoggingOptions.SectionName, new LoggingOptions()),
            (AircraftProfilesOptions.SectionName, new AircraftProfilesOptions()),
        ];

        var current = file.Read();
        var missing = new List<(string Section, string Key, JsonNode? Value)>();

        foreach (var (name, defaults) in sections)
        {
            if (JsonSerializer.SerializeToNode(defaults, SerializeOptions) is not JsonObject defaultsNode)
            {
                continue;
            }

            var existing = current[name] as JsonObject;
            foreach (var (key, value) in defaultsNode)
            {
                if (existing is null || !existing.ContainsKey(key))
                {
                    missing.Add((name, key, value?.DeepClone()));
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
