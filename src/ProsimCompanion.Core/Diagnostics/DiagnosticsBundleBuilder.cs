using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace ProsimCompanion.Core.Diagnostics;

/// <summary>What to bundle. Paths are passed in (not read from <c>UserDataPaths</c>) so the
/// builder is testable against a temp directory and never reaches outside what it is given.</summary>
public sealed record DiagnosticsBundleRequest(
    string SessionsDirectory,
    string LogsDirectory,
    string? SettingsPath,
    IAppBuildInfo Build)
{
    /// <summary>Newest session files to include (clamped to 1..20).</summary>
    public int SessionCount { get; init; } = 5;

    /// <summary>Newest daily app logs to include (clamped to 1..14).</summary>
    public int LogDays { get; init; } = 3;

    /// <summary>Also include the wire-trace logs for the same days. Off by default — huge.</summary>
    public bool IncludeWireTrace { get; init; }

    /// <summary>Extra lines for versions.txt (optional dependencies seen at startup).</summary>
    public IReadOnlyList<string> DependencyLines { get; init; } = [];

    /// <summary>Total size of source files allowed in (uncompressed); beyond it the oldest
    /// sessions, then the oldest logs, are dropped and listed in the manifest.</summary>
    public long MaxTotalBytes { get; init; } = DiagnosticsBundleBuilder.DefaultMaxTotalBytes;

    /// <summary>The bundle's timestamp (UTC); defaults to now. Injectable for tests.</summary>
    public DateTimeOffset? CreatedUtc { get; init; }
}

/// <summary>One file inside the bundle, as listed in manifest.json.</summary>
public sealed record BundleFileEntry(string Path, long Bytes, string Sha256);

/// <summary>A file that was selected but left out to respect the size cap.</summary>
public sealed record BundleTruncatedEntry(string Path, long Bytes, string Reason);

/// <summary>manifest.json — what the support side reads first. The hash list detects a
/// bundle that was edited or cut short in transit.</summary>
public sealed record BundleManifest(
    int BundleVersion,
    DateTimeOffset CreatedUtc,
    string AppVersion,
    string InformationalVersion,
    string? Commit,
    string Runtime,
    string Os,
    string Architecture,
    IReadOnlyList<BundleFileEntry> Files,
    IReadOnlyList<BundleTruncatedEntry> Truncated);

/// <summary>
/// Builds the support zip: one top-level folder holding manifest.json, sessions/, logs/,
/// config/settings.json (redacted) and versions.txt. Core-only (System.IO.Compression is
/// BCL) so the reducer tool can share the manifest types. Everything it reads is confined to
/// the directories in the request; the web access token, user content under
/// %LOCALAPPDATA%\ProsimCompanion\config\ and the wire trace (unless asked) never go in.
/// Files are opened with FileShare.ReadWrite because the current session and today's log
/// are still being written — the bytes and hash in the manifest describe what was copied.
/// </summary>
public sealed partial class DiagnosticsBundleBuilder
{
    /// <summary>200 MB of source bytes; beyond this the bundle is no longer mailable.</summary>
    public const long DefaultMaxTotalBytes = 200L * 1024 * 1024;

    /// <summary>Manifest schema version — bump when the layout changes.</summary>
    public const int BundleVersion = 1;

    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly ILogger<DiagnosticsBundleBuilder> _logger;

    public DiagnosticsBundleBuilder(ILogger<DiagnosticsBundleBuilder> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>The top-level folder name for a bundle created at <paramref name="createdUtc"/>.</summary>
    public static string FolderName(DateTimeOffset createdUtc)
        => $"ProsimCompanion-diagnostics-{createdUtc.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}";

    /// <summary>Writes the zip to a temp path and returns it; the caller owns the file.</summary>
    public async Task<string> BuildAsync(DiagnosticsBundleRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var createdUtc = request.CreatedUtc ?? DateTimeOffset.UtcNow;
        var folder = FolderName(createdUtc);
        var zipPath = Path.Combine(Path.GetTempPath(), $"{folder}-{Guid.NewGuid():N}.zip");

        var selection = Select(request);
        var files = new List<BundleFileEntry>();

        await using (var zipStream = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: false))
        {
            foreach (var (source, relative) in selection.Included)
            {
                ct.ThrowIfCancellationRequested();
                var entry = await CopyFileAsync(zip, source, $"{folder}/{relative}", ct).ConfigureAwait(false);
                if (entry is not null)
                {
                    files.Add(entry);
                }
            }

            var settings = ReadRedactedSettings(request.SettingsPath);
            if (settings is not null)
            {
                files.Add(await WriteTextAsync(zip, $"{folder}/config/settings.json", settings, ct).ConfigureAwait(false));
            }

            files.Add(await WriteTextAsync(
                zip, $"{folder}/versions.txt", VersionsText(request), ct).ConfigureAwait(false));

            var manifest = new BundleManifest(
                BundleVersion,
                createdUtc,
                request.Build.Version,
                request.Build.InformationalVersion,
                request.Build.Commit,
                request.Build.Runtime,
                request.Build.Os,
                request.Build.Architecture,
                files,
                selection.Truncated);
            await WriteTextAsync(
                zip, $"{folder}/manifest.json", JsonSerializer.Serialize(manifest, ManifestJson), ct).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "Diagnostics bundle built: {Files} file(s), {Dropped} dropped for size, {Path}",
            files.Count, selection.Truncated.Count, zipPath);
        return zipPath;
    }

    /// <summary>The selection step, exposed for tests: which files go in (source path and
    /// bundle-relative path) and which were dropped to respect the cap.</summary>
    public static BundleSelection Select(DiagnosticsBundleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sessionCount = Math.Clamp(request.SessionCount, 1, 20);
        var logDays = Math.Clamp(request.LogDays, 1, 14);

        // Newest first everywhere; dropping walks from the END (oldest).
        var sessions = ListFiles(request.SessionsDirectory, "session-*.jsonl")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ThenByDescending(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Take(sessionCount)
            .Select(f => (File: f, Relative: $"sessions/{f.Name}"))
            .ToList();

        var logs = SelectLogs(request.LogsDirectory, logDays, request.IncludeWireTrace)
            .Select(f => (File: f, Relative: $"logs/{f.Name}"))
            .ToList();

        var truncated = new List<BundleTruncatedEntry>();
        long total = sessions.Sum(s => s.File.Length) + logs.Sum(l => l.File.Length);
        var cap = Math.Max(request.MaxTotalBytes, 0);
        while (total > cap)
        {
            // Oldest sessions go first (keeping the newest as long as possible), then the
            // oldest logs, and only then the last session.
            (FileInfo File, string Relative) victim;
            if (sessions.Count > 1)
            {
                victim = sessions[^1];
                sessions.RemoveAt(sessions.Count - 1);
            }
            else if (logs.Count > 0)
            {
                victim = logs[^1];
                logs.RemoveAt(logs.Count - 1);
            }
            else if (sessions.Count == 1)
            {
                victim = sessions[0];
                sessions.Clear();
            }
            else
            {
                break;
            }

            total -= victim.File.Length;
            truncated.Add(new BundleTruncatedEntry(
                victim.Relative, victim.File.Length, "dropped to keep the bundle under the size cap"));
        }

        var included = sessions.Concat(logs).Select(x => (x.File.FullName, x.Relative)).ToList();
        return new BundleSelection(included, truncated);
    }

    /// <summary>App logs (and wire logs when asked) for the newest <paramref name="days"/>
    /// distinct dates, newest first. A day that rolled over on size ("_001") keeps every part.</summary>
    private static List<FileInfo> SelectLogs(string directory, int days, bool includeWire)
    {
        var candidates = ListFiles(directory, "ProsimCompanion-*.log")
            .Select(f => (File: f, Match: LogNamePattern().Match(f.Name)))
            .Where(x => x.Match.Success)
            .Where(x => includeWire || !x.Match.Groups["wire"].Success)
            .ToList();

        var dates = candidates
            .Select(x => x.Match.Groups["date"].Value)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(d => d, StringComparer.Ordinal)
            .Take(days)
            .ToHashSet(StringComparer.Ordinal);

        return candidates
            .Where(x => dates.Contains(x.Match.Groups["date"].Value))
            .OrderByDescending(x => x.Match.Groups["date"].Value, StringComparer.Ordinal)
            .ThenBy(x => x.Match.Groups["wire"].Success) // app log before its wire twin
            .ThenBy(x => x.File.Name, StringComparer.Ordinal)
            .Select(x => x.File)
            .ToList();
    }

    private static IEnumerable<FileInfo> ListFiles(string directory, string pattern)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return [];
        }

        return new DirectoryInfo(directory).EnumerateFiles(pattern, SearchOption.TopDirectoryOnly);
    }

    private static string? ReadRedactedSettings(string? settingsPath)
    {
        if (string.IsNullOrWhiteSpace(settingsPath) || !File.Exists(settingsPath))
        {
            return null;
        }

        using var stream = new FileStream(settingsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return SettingsRedactor.Redact(reader.ReadToEnd());
    }

    private static string VersionsText(DiagnosticsBundleRequest request)
    {
        var builder = new StringBuilder();
        builder.AppendLine(request.Build.Describe());
        builder.Append("Informational version: ").AppendLine(request.Build.InformationalVersion);
        foreach (var line in request.DependencyLines)
        {
            builder.AppendLine(line);
        }

        return builder.ToString();
    }

    private async Task<BundleFileEntry?> CopyFileAsync(ZipArchive zip, string sourcePath, string entryName, CancellationToken ct)
    {
        FileStream source;
        try
        {
            source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Diagnostics bundle: could not read {Path}; left out", sourcePath);
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Diagnostics bundle: could not read {Path}; left out", sourcePath);
            return null;
        }

        await using (source.ConfigureAwait(false))
        {
            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            await using var target = entry.Open();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long bytes = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                bytes += read;
            }

            return new BundleFileEntry(
                RelativePath(entryName), bytes, Convert.ToHexStringLower(hash.GetHashAndReset()));
        }
    }

    private static async Task<BundleFileEntry> WriteTextAsync(ZipArchive zip, string entryName, string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        await using (var target = entry.Open())
        {
            await target.WriteAsync(bytes, ct).ConfigureAwait(false);
        }

        return new BundleFileEntry(RelativePath(entryName), bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    /// <summary>Manifest paths are relative to the top-level folder.</summary>
    private static string RelativePath(string entryName)
    {
        var slash = entryName.IndexOf('/', StringComparison.Ordinal);
        return slash >= 0 ? entryName[(slash + 1)..] : entryName;
    }

    [GeneratedRegex(@"^ProsimCompanion-(?<wire>wire-)?(?<date>\d{8})(_\d+)?\.log$", RegexOptions.IgnoreCase)]
    private static partial Regex LogNamePattern();
}

/// <summary>Result of <see cref="DiagnosticsBundleBuilder.Select"/>.</summary>
public sealed record BundleSelection(
    IReadOnlyList<(string SourcePath, string RelativePath)> Included,
    IReadOnlyList<BundleTruncatedEntry> Truncated);
