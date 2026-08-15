using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ProsimCompanion.Core.Configuration;

/// <summary>Outcome counts of one seeding pass — logged by the caller.</summary>
/// <param name="Seeded">Files newly copied into the user tree (missing there before).</param>
/// <param name="Updated">Unedited files refreshed because the shipped content changed.</param>
/// <param name="KeptEdited">User-edited files left untouched despite newer shipped content.</param>
/// <param name="Current">Files already matching the shipped content.</param>
public sealed record UserConfigSeedResult(int Seeded, int Updated, int KeptEdited, int Current);

/// <summary>
/// Mirrors the shipped user-editable config defaults from <c>{app}\config</c> into
/// <see cref="UserConfigPaths.Root"/> with keep-user-edits semantics (ADR-0007, issue #55) —
/// the same three-way decision the installer applies to GSX profiles:
/// <list type="bullet">
/// <item>missing in the user tree → copy the shipped file (also migrates edits a user made
/// beside the exe under the pre-ADR-0007 layout, because that file IS the shipped source);</item>
/// <item>matches the shipped content → nothing to do (and the manifest adopts it, so a future
/// shipped change auto-updates it);</item>
/// <item>differs from shipped AND from the last-seeded hash → user-edited, kept;</item>
/// <item>differs from shipped but equals the last-seeded hash → never edited, refreshed.</item>
/// </list>
/// "Last-seeded" lives in a manifest beside the files
/// (<c>.shipped-manifest.json</c>: relative path → SHA-256). A user file with no shipped
/// counterpart (own checklists, own sets) is never touched or deleted. Every failure degrades:
/// a seeding problem logs and leaves the app running on whatever content is present.
/// </summary>
public static class UserConfigSeeder
{
    /// <summary>Entries under <c>config\</c> that are user-editable content (per the csproj
    /// Content comments). Directories are mirrored recursively (<c>*.json</c>).
    /// <c>settings.json</c> and <c>techlog\wear-pool.json</c> are deliberately absent —
    /// installer-owned and app-owned respectively.</summary>
    private static readonly string[] UserEditableEntries =
    [
        "checklists",
        "abnormals",
        "themes", // nothing shipped (built-ins are embedded) — entry migrates old drop-ins
        "atc-requests.json",
        "commands.json",
        "phrases.json",
    ];

    private const string ManifestFileName = ".shipped-manifest.json";

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>Seeds the default entry set from the shipped config folder into the user root.</summary>
    public static UserConfigSeedResult Seed(string shippedRoot, string userRoot, ILogger logger)
        => Seed(shippedRoot, userRoot, UserEditableEntries, logger);

    /// <summary>Seeds explicit entries — the test seam.</summary>
    public static UserConfigSeedResult Seed(
        string shippedRoot, string userRoot, IReadOnlyList<string> entries, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shippedRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(userRoot);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(logger);

        var seeded = 0;
        var updated = 0;
        var keptEdited = 0;
        var current = 0;

        try
        {
            Directory.CreateDirectory(userRoot);
            var manifest = LoadManifest(userRoot, logger);

            foreach (var relative in EnumerateShippedFiles(shippedRoot, entries))
            {
                try
                {
                    var shippedFile = Path.Combine(shippedRoot, relative);
                    var userFile = Path.Combine(userRoot, relative);
                    var shippedHash = HashFile(shippedFile);

                    if (!File.Exists(userFile))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(userFile)!);
                        File.Copy(shippedFile, userFile);
                        manifest[relative] = shippedHash;
                        seeded++;
                        continue;
                    }

                    var userHash = HashFile(userFile);
                    if (string.Equals(userHash, shippedHash, StringComparison.OrdinalIgnoreCase))
                    {
                        // Adopting the hash here is what lets a legacy or hand-copied file
                        // start receiving automatic default updates from now on.
                        manifest[relative] = shippedHash;
                        current++;
                        continue;
                    }

                    if (manifest.TryGetValue(relative, out var lastSeeded)
                        && string.Equals(lastSeeded, userHash, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Copy(shippedFile, userFile, overwrite: true);
                        manifest[relative] = shippedHash;
                        updated++;
                        continue;
                    }

                    // No manifest entry (unknown provenance) counts as user-edited too —
                    // conservative, matching the installer's GSX-profile rule.
                    keptEdited++;
                    logger.LogInformation(
                        "User-edited config file kept: {Relative} (shipped default differs)", relative);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(ex, "Seeding config file {Relative} failed — skipped", relative);
                }
            }

            SaveManifest(userRoot, manifest, logger);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex,
                "User config seeding into {UserRoot} failed — running on existing content", userRoot);
        }

        return new UserConfigSeedResult(seeded, updated, keptEdited, current);
    }

    private static IEnumerable<string> EnumerateShippedFiles(
        string shippedRoot, IReadOnlyList<string> entries)
    {
        foreach (var entry in entries)
        {
            var absolute = Path.Combine(shippedRoot, entry);
            if (File.Exists(absolute))
            {
                yield return entry;
            }
            else if (Directory.Exists(absolute))
            {
                foreach (var file in Directory.EnumerateFiles(absolute, "*.json", SearchOption.AllDirectories))
                {
                    yield return Path.GetRelativePath(shippedRoot, file);
                }
            }

            // A shipped entry absent from this build is simply not seeded — never an error.
        }
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static Dictionary<string, string> LoadManifest(string userRoot, ILogger logger)
    {
        var path = Path.Combine(userRoot, ManifestFileName);
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    File.ReadAllText(path));
                if (loaded is not null)
                {
                    return new Dictionary<string, string>(loaded, StringComparer.OrdinalIgnoreCase);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A lost manifest only means every locally-diverged file counts as user-edited
            // until it next matches shipped — safe in the keep-edits direction.
            logger.LogWarning(ex, "Seed manifest {Path} unreadable — treating diverged files as user-edited", path);
        }

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private static void SaveManifest(string userRoot, Dictionary<string, string> manifest, ILogger logger)
    {
        var path = Path.Combine(userRoot, ManifestFileName);
        try
        {
            // Temp-then-move so a crash mid-write can't leave a torn manifest (DayStateFile rule).
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(manifest, ManifestJsonOptions));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Seed manifest write failed — next run re-derives from content hashes");
        }
    }
}
