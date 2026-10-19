using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// The one enum ↔ settings-text bridge (candidate C3, 2026-10-19). The settings file and the
/// web dropdowns speak the camelCase JSON name of an enum member (<see cref="SettingsJson"/>:
/// <c>"openAi"</c>, <c>"fasterWhisper"</c>); the option classes hold the enum. Four settings
/// pages used to carry one hand-written property per enum for this (<c>LlmApiKey</c>,
/// <c>AsrApiKey</c>, …); the <c>OptionField</c> renderer calls this instead, for every enum.
/// </summary>
public static class EnumText
{
    /// <summary>The camelCase JSON name of an enum value ("OpenAi" → "openAi").</summary>
    public static string ToJsonName(object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return JsonNamingPolicy.CamelCase.ConvertName(value.ToString() ?? "");
    }

    /// <summary>Parses a member name case-insensitively ("openai", "OpenAi" and "openAi" all
    /// read as <c>OpenAi</c>) — the config binder's own leniency, so a hand-edited file and the
    /// dropdown agree. False for a name the enum lacks.</summary>
    public static bool TryParse(Type enumType, string? text, [NotNullWhen(true)] out object? value)
    {
        ArgumentNullException.ThrowIfNull(enumType);
        value = null;
        if (string.IsNullOrWhiteSpace(text) || !enumType.IsEnum)
        {
            return false;
        }

        if (Enum.TryParse(enumType, text.Trim(), ignoreCase: true, out var parsed) && parsed is not null
            && Enum.IsDefined(enumType, parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }

    /// <summary>Parses a member name, or returns <paramref name="fallback"/> for a name the
    /// enum lacks — the dropdown can never put an undefined value into a draft.</summary>
    public static object Parse(Type enumType, string? text, object fallback)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        return TryParse(enumType, text, out var value) ? value : fallback;
    }

    /// <summary>Typed <see cref="Parse(Type, string?, object)"/>.</summary>
    public static TEnum Parse<TEnum>(string? text, TEnum fallback)
        where TEnum : struct, Enum
        => (TEnum)Parse(typeof(TEnum), text, fallback);
}
