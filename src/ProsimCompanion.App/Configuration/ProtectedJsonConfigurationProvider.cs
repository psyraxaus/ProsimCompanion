using System.IO;
using Microsoft.Extensions.Configuration.Json;
using ProsimCompanion.Core.Configuration;
using Serilog;

namespace ProsimCompanion.App.Configuration;

/// <summary>
/// Loads settings.json like the stock JSON provider, then swaps every registered secret path
/// for its decrypted value. A value that cannot be decrypted on this PC/user binds as "" —
/// the same as "not configured" — and the path is published through
/// <see cref="SecretProtector.Unreadable"/> so the settings page can ask for it again.
/// </summary>
public sealed class ProtectedJsonConfigurationProvider : JsonConfigurationProvider
{
    public ProtectedJsonConfigurationProvider(ProtectedJsonConfigurationSource source)
        : base(source)
    {
    }

    /// <inheritdoc />
    public override void Load(Stream stream)
    {
        base.Load(stream);

        var unreadable = new List<string>();
        foreach (var path in SecretProtector.SecretPaths)
        {
            // Data is case-insensitive, so the camelCase path finds a hand-edited "ApiKey" too.
            if (!Data.TryGetValue(path, out var stored) || string.IsNullOrEmpty(stored))
            {
                continue;
            }

            if (SecretProtector.TryUnprotect(stored, out var plain))
            {
                Data[path] = plain;
            }
            else
            {
                Data[path] = "";
                unreadable.Add(path);
            }
        }

        if (unreadable.Count > 0)
        {
            // Logged here rather than by a DI logger: configuration loads before the container
            // exists (and again on every file-watcher reload). Paths only — never the value.
            Log.Warning(
                "{Count} protected setting(s) could not be decrypted on this PC/user and are treated as not configured: {Paths}. Re-enter them on the settings page.",
                unreadable.Count,
                unreadable);
        }

        // Always, even when empty: a re-entered key must clear its earlier failure.
        SecretProtector.RecordLoad(unreadable);
    }
}
