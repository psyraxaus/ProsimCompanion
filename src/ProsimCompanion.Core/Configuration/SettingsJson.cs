using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// The one place the settings-file JSON shape is defined: camelCase keys, enums as camelCase
/// strings (the file is hand-editable and the config binder parses enum names
/// case-insensitively). The defaults writer, the settings writer and the page drafts all
/// serialize through here so a value written by one is always readable by the others.
/// </summary>
public static class SettingsJson
{
    /// <summary>File-facing serialization: null-valued keys are omitted, so an unset optional
    /// (e.g. an empty API key) never self-documents as <c>"key": null</c>.</summary>
    public static JsonSerializerOptions FileOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Draft/diff serialization: nulls are kept, so clearing a value diffs against its
    /// baseline and persists as an explicit JSON null (the pre-#84 page behaviour).</summary>
    public static JsonSerializerOptions DraftOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
