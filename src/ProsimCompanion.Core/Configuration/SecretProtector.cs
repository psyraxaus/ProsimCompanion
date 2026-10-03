using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// Keeps the API keys and tokens in config/settings.json out of clear text. Values are wrapped
/// with Windows DPAPI in <see cref="DataProtectionScope.CurrentUser"/> scope and stored as
/// <c>dpapi:&lt;base64&gt;</c>; everything else in the file stays hand-editable JSON.
/// </summary>
/// <remarks>
/// <para>Why DPAPI: the file sits beside the exe and gets copied into bug reports, zips and
/// backups. A key tied to the Windows user account is worthless on another PC or account,
/// which is exactly the failure mode we want — and why a settings file copied elsewhere shows
/// a "re-enter the key" hint instead of a value.</para>
/// <para>Two write paths meet here: <see cref="JsonSettingsFile.Update"/> protects on every
/// write (so the web UI, the predecessor importer and the token generator get it for free),
/// and <see cref="EnsureProtected"/> is the one-shot startup pass that upgrades a file written
/// by an older build. The read side (<c>ProtectedJsonConfigurationProvider</c> in the App
/// project) decrypts into the configuration tree, so every <c>IOptionsMonitor</c> consumer
/// still sees the plain value.</para>
/// <para>Plain values are always accepted on read (a user may still hand-edit a key into the
/// file); they are upgraded at the next startup or write. DPAPI output is non-deterministic,
/// so nothing may compare stored strings for equality — compare decrypted option values.</para>
/// </remarks>
public static class SecretProtector
{
    /// <summary>Marker that identifies a protected value in the file.</summary>
    public const string Prefix = "dpapi:";

    // Fixed additional entropy: not a secret, just a namespace so a blob protected by another
    // app on the same account cannot be fed to us (and vice versa). Bump the suffix if the
    // layout ever changes so old blobs fail cleanly instead of decoding to garbage.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ProsimCompanion.settings.v1");

    private static volatile IReadOnlySet<string> _unreadable =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Every config path (<c>section:key</c>, the configuration-tree spelling) that holds a
    /// secret. Adding a key here is the whole registration: the file writer protects it, the
    /// provider decrypts it, the settings page can ask <see cref="Unreadable"/> about it.
    /// </summary>
    public static IReadOnlyList<string> SecretPaths { get; } =
    [
        "prosim:apiKey",
        "sayIntentions:manualApiKey",
        "webUi:accessToken",
        "briefing:llmApiKey",
        "speech:elevenLabsApiKey",
        "webUi:https:pfxPassword",
        // Issue #151: a notification target's URL carries its secret (a Discord webhook URL,
        // an ntfy topic nobody should learn) as much as its token does. `*` = every target.
        "notifications:targets:*:url",
        "notifications:targets:*:token",
    ];

    /// <summary>
    /// Config paths whose stored value could not be decrypted at the last configuration load
    /// (the file was copied from another PC or Windows account). Those options bind as empty;
    /// the settings pages show a re-enter hint next to the field. Replaced wholesale on every
    /// load, so it clears as soon as the user saves a new value.
    /// </summary>
    public static IReadOnlySet<string> Unreadable => _unreadable;

    /// <summary>True when <paramref name="stored"/> carries the protected-value marker.</summary>
    public static bool IsProtected(string? stored)
        => stored is not null && stored.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>
    /// Wraps a plain value for storage. A no-op for empty values, values already protected and
    /// on non-Windows platforms (DPAPI does not exist there; the value stays plain so the app —
    /// and the test suite — keep working).
    /// </summary>
    public static string Protect(string value)
    {
        if (string.IsNullOrEmpty(value) || IsProtected(value) || !OperatingSystem.IsWindows())
        {
            return value;
        }

        var cipher = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
        return Prefix + Convert.ToBase64String(cipher);
    }

    /// <summary>
    /// Resolves a stored value to its plain form. Unprotected values pass through unchanged
    /// (including null/empty). Returns false — with <paramref name="value"/> null — when a
    /// protected value cannot be decrypted here: another user/PC, a corrupt blob, or a platform
    /// without DPAPI.
    /// </summary>
    public static bool TryUnprotect(string? stored, out string? value)
    {
        if (!IsProtected(stored))
        {
            value = stored;
            return true;
        }

        try
        {
            var cipher = Convert.FromBase64String(stored![Prefix.Length..]);
            if (!OperatingSystem.IsWindows())
            {
                // ProtectedData throws PlatformNotSupportedException here anyway; the explicit
                // check keeps the CA1416 analyzer honest about the call below.
                value = null;
                return false;
            }

            var plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
            value = Encoding.UTF8.GetString(plain);
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or PlatformNotSupportedException)
        {
            value = null;
            return false;
        }
    }

    /// <summary>
    /// Protects every plain secret found in a settings document in place. Values already
    /// protected, absent, empty or non-string are left alone. Returns true when anything changed.
    /// </summary>
    public static bool ProtectKnownSecrets(JsonObject root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var changed = false;
        foreach (var path in SecretPaths)
        {
            foreach (var (parent, key) in LocateAll(root, path))
            {
                if (parent[key] is not JsonValue leaf || !leaf.TryGetValue<string>(out var plain))
                {
                    continue;
                }

                var protectedValue = Protect(plain);
                if (!ReferenceEquals(protectedValue, plain))
                {
                    parent[key] = protectedValue;
                    changed = true;
                }
            }
        }

        return changed;
    }

    /// <summary>
    /// Startup pass: upgrades a settings file that still holds a plain secret (written by an
    /// older build or by hand). Reads first and writes only when something was plain, so a
    /// steady-state start never rewrites the file. Returns true when the file was written.
    /// </summary>
    public static bool EnsureProtected(JsonSettingsFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (!HasPlainSecret(file.Read()))
        {
            return false;
        }

        // Update itself runs ProtectKnownSecrets after the mutation; nothing else to change.
        file.Update(_ => { });
        return true;
    }

    /// <summary>
    /// Publishes the set of paths that failed to decrypt during a configuration load. Called by
    /// the configuration provider at the end of every load (including file-watcher reloads),
    /// so a re-entered key clears its entry.
    /// </summary>
    public static void RecordLoad(IEnumerable<string> unreadablePaths)
    {
        ArgumentNullException.ThrowIfNull(unreadablePaths);
        _unreadable = new HashSet<string>(unreadablePaths, StringComparer.OrdinalIgnoreCase);
    }

    private static bool HasPlainSecret(JsonObject root)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        foreach (var path in SecretPaths)
        {
            foreach (var (parent, key) in LocateAll(root, path))
            {
                if (parent[key] is JsonValue leaf
                    && leaf.TryGetValue<string>(out var value)
                    && value.Length > 0
                    && !IsProtected(value))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Walks <c>section:sub:key</c> without creating anything and yields every (parent, key)
    /// the path names. A <c>*</c> segment stands for every element of a JSON array (issue
    /// #151: <c>notifications:targets:*:token</c> — one secret per list entry), so a path may
    /// resolve to zero, one or many leaves.
    /// </summary>
    private static IEnumerable<(JsonObject Parent, string Key)> LocateAll(JsonObject root, string path)
    {
        var segments = path.Split(':');
        return Walk(root, segments, 0);

        static IEnumerable<(JsonObject Parent, string Key)> Walk(JsonObject node, string[] segments, int index)
        {
            if (index == segments.Length - 1)
            {
                yield return (node, segments[index]);
                yield break;
            }

            var segment = segments[index];
            if (segment == "*")
            {
                yield break; // a wildcard must follow a list-valued key, never an object key
            }

            switch (node[segment])
            {
                case JsonObject child:
                    foreach (var hit in Walk(child, segments, index + 1))
                    {
                        yield return hit;
                    }

                    break;

                case JsonArray list when index + 1 < segments.Length - 1 && segments[index + 1] == "*":
                    foreach (var element in list)
                    {
                        if (element is JsonObject item)
                        {
                            foreach (var hit in Walk(item, segments, index + 2))
                            {
                                yield return hit;
                            }
                        }
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// The concrete configuration keys a secret path names in a flattened configuration
    /// (<c>notifications:targets:*:token</c> → <c>notifications:targets:0:token</c>, …): the
    /// provider decrypts by key, so wildcards are expanded against the keys it loaded. Paths
    /// without a wildcard return themselves. Case-insensitive, like configuration keys.
    /// </summary>
    public static IEnumerable<string> ExpandSecretKeys(IEnumerable<string> loadedKeys)
    {
        ArgumentNullException.ThrowIfNull(loadedKeys);
        var keys = loadedKeys as ICollection<string> ?? [.. loadedKeys];
        foreach (var path in SecretPaths)
        {
            if (!path.Contains('*', StringComparison.Ordinal))
            {
                yield return path;
                continue;
            }

            var pattern = "^" + string.Join(':', path.Split(':').Select(s => s == "*" ? "[0-9]+" : Regex.Escape(s))) + "$";
            foreach (var key in keys)
            {
                if (Regex.IsMatch(key, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                {
                    yield return key;
                }
            }
        }
    }
}
