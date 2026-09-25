using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using ProsimCompanion.Core.Diagnostics;

namespace ProsimCompanion.Reduce;

/// <summary>The bundle's files on disk after validation, plus what the manifest said.</summary>
public sealed record BundleContents(
    string Source,
    string Root,
    string? Folder,
    BundleManifest? Manifest,
    IReadOnlyList<string> SessionFiles,
    IReadOnlyList<string> LogFiles,
    bool HashesVerified,
    IReadOnlyList<string> Notes) : IDisposable
{
    /// <summary>Temp extraction directory to remove when the reader was given a zip.</summary>
    public string? TempDirectory { get; init; }

    public void Dispose()
    {
        if (TempDirectory is not null && Directory.Exists(TempDirectory))
        {
            try
            {
                Directory.Delete(TempDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}

/// <summary>Thrown when the bundle must be refused (exit code 2); the message says why.</summary>
public sealed class BundleRejectedException(string message) : Exception(message);

/// <summary>
/// Opens a bundle zip or a bare folder and refuses anything that is not a plain diagnostics
/// bundle: an entry escaping the extraction root, an extension other than .jsonl/.log/.json/
/// .txt, a single entry over 512 MB, more than 500 entries, or a manifest whose SHA-256 list
/// does not match the bytes. The support host runs this on user-supplied files, so the
/// checks run BEFORE anything is extracted.
/// </summary>
public static class BundleReader
{
    public const long MaxEntryBytes = 512L * 1024 * 1024;
    public const int MaxEntries = 500;

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jsonl", ".log", ".json", ".txt",
    };

    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static BundleContents Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Directory.Exists(path))
        {
            return FromFolder(Path.GetFullPath(path), source: "folder", tempDirectory: null);
        }

        if (!File.Exists(path))
        {
            throw new BundleRejectedException($"'{path}' is neither a zip file nor a folder.");
        }

        var temp = Path.Combine(Path.GetTempPath(), $"reduce-{Guid.NewGuid():N}");
        try
        {
            ExtractValidated(path, temp);
            return FromFolder(temp, source: "zip", tempDirectory: temp);
        }
        catch
        {
            try
            {
                if (Directory.Exists(temp))
                {
                    Directory.Delete(temp, recursive: true);
                }
            }
            catch (IOException)
            {
            }

            throw;
        }
    }

    /// <summary>Validates every entry, then extracts. Exposed for tests.</summary>
    public static void ExtractValidated(string zipPath, string destination)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        if (zip.Entries.Count > MaxEntries)
        {
            throw new BundleRejectedException($"Bundle has {zip.Entries.Count} entries; the limit is {MaxEntries}.");
        }

        var root = Path.GetFullPath(destination + Path.DirectorySeparatorChar);
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.EndsWith('/'))
            {
                continue; // directory marker
            }

            if (Path.IsPathRooted(name) || name.Split('/').Any(segment => segment is ".." or "."))
            {
                throw new BundleRejectedException($"Entry '{entry.FullName}' escapes the extraction root.");
            }

            var target = Path.GetFullPath(Path.Combine(destination, name));
            if (!target.StartsWith(root, StringComparison.Ordinal))
            {
                throw new BundleRejectedException($"Entry '{entry.FullName}' escapes the extraction root.");
            }

            if (!AllowedExtensions.Contains(Path.GetExtension(name)))
            {
                throw new BundleRejectedException($"Entry '{entry.FullName}' is not a .jsonl/.log/.json/.txt file.");
            }

            if (entry.Length > MaxEntryBytes)
            {
                throw new BundleRejectedException($"Entry '{entry.FullName}' is {entry.Length} bytes; the limit is {MaxEntryBytes}.");
            }
        }

        Directory.CreateDirectory(destination);
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.EndsWith('/'))
            {
                continue;
            }

            var target = Path.Combine(destination, name);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: false);
        }
    }

    private static BundleContents FromFolder(string root, string source, string? tempDirectory)
    {
        var notes = new List<string>();
        var manifestPath = Directory.EnumerateFiles(root, "manifest.json", SearchOption.AllDirectories)
            .OrderBy(p => p.Length)
            .FirstOrDefault();

        BundleManifest? manifest = null;
        string? folder = null;
        var hashesVerified = false;
        var sessions = new List<string>();
        var logs = new List<string>();

        if (manifestPath is not null)
        {
            var manifestRoot = Path.GetDirectoryName(manifestPath)!;
            folder = Path.GetFileName(manifestRoot);
            try
            {
                manifest = JsonSerializer.Deserialize<BundleManifest>(File.ReadAllText(manifestPath), ManifestJson);
            }
            catch (JsonException ex)
            {
                throw new BundleRejectedException($"manifest.json did not parse: {ex.Message}");
            }

            if (manifest is null)
            {
                throw new BundleRejectedException("manifest.json is empty.");
            }

            foreach (var file in manifest.Files)
            {
                var full = Path.GetFullPath(Path.Combine(manifestRoot, file.Path.Replace('/', Path.DirectorySeparatorChar)));
                if (!full.StartsWith(Path.GetFullPath(manifestRoot + Path.DirectorySeparatorChar), StringComparison.Ordinal))
                {
                    throw new BundleRejectedException($"Manifest path '{file.Path}' escapes the bundle folder.");
                }

                if (!File.Exists(full))
                {
                    throw new BundleRejectedException($"Manifest lists '{file.Path}' but the bundle does not contain it.");
                }

                var actual = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(full)));
                if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new BundleRejectedException($"SHA-256 mismatch for '{file.Path}': the bundle was altered or truncated.");
                }

                Classify(full, sessions, logs);
            }

            hashesVerified = true;
        }
        else
        {
            notes.Add("no manifest.json: bare folder scanned, hashes not verified");
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                Classify(file, sessions, logs);
            }
        }

        sessions.Sort(StringComparer.OrdinalIgnoreCase);
        logs.Sort(StringComparer.OrdinalIgnoreCase);
        return new BundleContents(source, root, folder, manifest, sessions, logs, hashesVerified, notes)
        {
            TempDirectory = tempDirectory,
        };
    }

    /// <summary>Any .jsonl is a session file and any non-wire .log a CMTrace log: the app names
    /// them session-*/ProsimCompanion-*, but a recording copied into a test folder or renamed by
    /// a user is still the same evidence.</summary>
    private static void Classify(string path, List<string> sessions, List<string> logs)
    {
        var name = Path.GetFileName(path);
        if (name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
        {
            sessions.Add(path);
        }
        else if (name.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
            && !name.Contains("-wire-", StringComparison.OrdinalIgnoreCase))
        {
            logs.Add(path);
        }
    }
}
