using System.Text.Json;

namespace ProsimCompanion.Core.Localization;

/// <summary>
/// One UI language: every translated string of the web UI, named by a dotted path
/// (<c>layout.nav.flightStatus</c>), read from one JSON file named after its language code
/// (<c>de.json</c>). The file may nest objects for readability — <c>{"layout": {"nav":
/// {"flightStatus": "…"}}}</c> — and is flattened on load, so a translator edits sections
/// while the code looks ids up flat (ADR-0014).
/// </summary>
public sealed class LanguagePack
{
    /// <summary>The reserved top-level member holding the pack's own description.</summary>
    public const string MetaMember = "$language";

    private LanguagePack(string code, string nativeName, string path, IReadOnlyDictionary<string, string> strings)
    {
        Code = code;
        NativeName = nativeName;
        Path = path;
        Strings = strings;
    }

    /// <summary>Language code the file is named with — a BCP 47 primary tag or tag-region
    /// (<c>en</c>, <c>de</c>, <c>pt-BR</c>), lower-cased for lookup.</summary>
    public string Code { get; }

    /// <summary>The language's name in itself ("Deutsch", "日本語") — what the picker shows,
    /// so a pilot who cannot read the current language still finds their own.</summary>
    public string NativeName { get; }

    /// <summary>Where the pack was read from (for the Appearance page hint and the log).</summary>
    public string Path { get; }

    /// <summary>Flattened text id → text.</summary>
    public IReadOnlyDictionary<string, string> Strings { get; }

    /// <summary>Reads and parses one pack file; the code is the file name without extension.</summary>
    /// <exception cref="JsonException">The file is not a valid pack (also for a non-string leaf).</exception>
    public static LanguagePack Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var code = System.IO.Path.GetFileNameWithoutExtension(path);
        return Parse(code, File.ReadAllText(path), path);
    }

    /// <summary>Parses pack JSON — the test seam, and what <see cref="Load"/> calls.</summary>
    /// <exception cref="JsonException">Not an object, or a leaf that is not a string.</exception>
    public static LanguagePack Parse(string code, string json, string path = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentNullException.ThrowIfNull(json);

        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"Language pack '{code}' must be a JSON object at the top level.");
        }

        var nativeName = code;
        var strings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var member in document.RootElement.EnumerateObject())
        {
            if (member.NameEquals(MetaMember))
            {
                if (member.Value.ValueKind == JsonValueKind.Object
                    && member.Value.TryGetProperty("name", out var name)
                    && name.ValueKind == JsonValueKind.String
                    && name.GetString() is { Length: > 0 } text)
                {
                    nativeName = text;
                }

                continue;
            }

            Flatten(member.Name, member.Value, strings, code);
        }

        return new LanguagePack(code.ToLowerInvariant(), nativeName, path, strings);
    }

    private static void Flatten(string prefix, JsonElement element, Dictionary<string, string> into, string code)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                into[prefix] = element.GetString() ?? "";
                break;
            case JsonValueKind.Object:
                foreach (var child in element.EnumerateObject())
                {
                    Flatten(prefix + "." + child.Name, child.Value, into, code);
                }

                break;
            default:
                // A number, bool, null or array under an id is a translator's slip — name
                // it precisely so the Logs page points at the id, not just the file.
                throw new JsonException(
                    $"Language pack '{code}': '{prefix}' must be a string or an object of strings, not {element.ValueKind}.");
        }
    }
}
