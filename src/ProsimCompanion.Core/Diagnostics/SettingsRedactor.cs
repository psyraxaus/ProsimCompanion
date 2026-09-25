using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ProsimCompanion.Core.Diagnostics;

/// <summary>
/// Produces the copy of settings.json that goes into a support bundle: every secret-bearing
/// value replaced, structure kept, so support can see which features were enabled without
/// ever seeing a key. Two rules, either of which redacts a string value:
/// <list type="bullet">
/// <item>the property name contains token / apiKey / api_key / secret / password /
/// credential / bearer (case-insensitive), or</item>
/// <item>the value itself looks like a credential: a bearer header, a JWT, a 32+ hex key,
/// a DPAPI blob, or one long space-free letters-and-digits token.</item>
/// </list>
/// The name rule applies to string values only: a boolean such as
/// <c>requireTokenOnLoopback</c> is a feature switch, not a secret, and support needs it.
/// </summary>
public static partial class SettingsRedactor
{
    /// <summary>The replacement value.</summary>
    public const string Mask = "***REDACTED***";

    private static readonly string[] SecretNameFragments =
    [
        "token", "apikey", "api_key", "secret", "password", "credential", "bearer",
    ];

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>True when a property with this name must never carry its value into a bundle.</summary>
    public static bool IsSecretName(string propertyName)
    {
        ArgumentNullException.ThrowIfNull(propertyName);
        return SecretNameFragments.Any(fragment =>
            propertyName.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True when a string value looks like a credential regardless of its key.</summary>
    public static bool LooksLikeSecret(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var trimmed = value.Trim();
        if (trimmed.Length < 16)
        {
            return false;
        }

        return trimmed.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("dpapi:", StringComparison.Ordinal)
            || JwtPattern().IsMatch(trimmed)
            || HexKeyPattern().IsMatch(trimmed)
            || (OpaqueTokenPattern().IsMatch(trimmed) && trimmed.Any(char.IsLetter) && trimmed.Any(char.IsDigit));
    }

    /// <summary>Redacts a settings document. Unparseable input yields a one-line note instead
    /// of the raw text — a broken file must not leak whatever it contained.</summary>
    public static string Redact(string settingsJson)
    {
        ArgumentNullException.ThrowIfNull(settingsJson);
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(settingsJson);
        }
        catch (JsonException)
        {
            return "{ \"redacted\": \"settings.json did not parse as JSON; content withheld\" }";
        }

        if (root is null)
        {
            return "null";
        }

        var redacted = RedactNode(root, parentName: null);
        return redacted.ToJsonString(WriteOptions);
    }

    private static JsonNode RedactNode(JsonNode node, string? parentName)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var copy = new JsonObject();
                foreach (var (name, child) in obj)
                {
                    copy[name] = child is null ? null : RedactNode(child, name);
                }

                return copy;
            }

            case JsonArray array:
            {
                var copy = new JsonArray();
                foreach (var child in array)
                {
                    copy.Add(child is null ? null : RedactNode(child, parentName));
                }

                return copy;
            }

            case JsonValue value when value.TryGetValue<string>(out var text):
                if (text.Length > 0 && ((parentName is not null && IsSecretName(parentName)) || LooksLikeSecret(text)))
                {
                    return JsonValue.Create(Mask)!;
                }

                return JsonValue.Create(text)!;

            default:
                return node.DeepClone();
        }
    }

    [GeneratedRegex(@"^eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]*$")]
    private static partial Regex JwtPattern();

    [GeneratedRegex(@"^[A-Fa-f0-9]{32,}$")]
    private static partial Regex HexKeyPattern();

    // One space-free run of key-alphabet characters (no dots, colons or slashes, so paths,
    // URLs, hosts and file names never match). 32+ chars: shorter values are words.
    [GeneratedRegex(@"^[A-Za-z0-9_\-+=]{32,}$")]
    private static partial Regex OpaqueTokenPattern();
}
