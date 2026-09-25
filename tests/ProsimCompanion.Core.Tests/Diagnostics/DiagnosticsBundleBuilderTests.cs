using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.Core.Diagnostics;
using Xunit;

namespace ProsimCompanion.Core.Tests.Diagnostics;

/// <summary>
/// The support zip against a temp user-data layout: newest-N / newest-D selection, manifest
/// hashes, redaction, the size cap's drop order, and missing directories degrading to an
/// empty (but valid) bundle.
/// </summary>
public sealed class DiagnosticsBundleBuilderTests : IDisposable
{
    private static readonly IAppBuildInfo Build = AppBuildInfo.Create("0.5.0-rc.3+abc1234", ".NET 10.0.1", "Windows 10", "X64");

    private readonly string _root = Directory.CreateTempSubdirectory("pc-bundle-").FullName;
    private readonly List<string> _zips = [];

    private string Sessions => Path.Combine(_root, "sessions");
    private string Logs => Path.Combine(_root, "logs");
    private string Settings => Path.Combine(_root, "settings.json");

    public void Dispose()
    {
        foreach (var zip in _zips)
        {
            try
            {
                File.Delete(zip);
            }
            catch (IOException)
            {
            }
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void Session(string stamp, int bytes = 100, DateTime? written = null)
    {
        Directory.CreateDirectory(Sessions);
        var path = Path.Combine(Sessions, $"session-{stamp}.jsonl");
        File.WriteAllText(path, new string('x', bytes));
        File.SetLastWriteTimeUtc(path, written ?? DateTime.ParseExact(stamp, "yyyyMMdd-HHmmss", null));
    }

    private void Log(string name, int bytes = 100)
    {
        Directory.CreateDirectory(Logs);
        File.WriteAllText(Path.Combine(Logs, name), new string('l', bytes));
    }

    private DiagnosticsBundleRequest Request(int sessions = 5, int days = 3, bool wire = false, long cap = DiagnosticsBundleBuilder.DefaultMaxTotalBytes)
        => new(Sessions, Logs, Settings, Build)
        {
            SessionCount = sessions,
            LogDays = days,
            IncludeWireTrace = wire,
            MaxTotalBytes = cap,
            DependencyLines = ["ProSim: Connected", "GSX: Disconnected"],
            CreatedUtc = new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero),
        };

    private async Task<(ZipArchive Zip, JsonElement Manifest, string Folder)> BuildAsync(DiagnosticsBundleRequest request)
    {
        var builder = new DiagnosticsBundleBuilder(NullLogger<DiagnosticsBundleBuilder>.Instance);
        var zipPath = await builder.BuildAsync(request, CancellationToken.None);
        _zips.Add(zipPath);
        var zip = ZipFile.OpenRead(zipPath);
        var folder = "ProsimCompanion-diagnostics-20260926-120000";
        var manifestEntry = zip.GetEntry($"{folder}/manifest.json");
        Assert.NotNull(manifestEntry);
        using var reader = new StreamReader(manifestEntry.Open());
        var manifest = JsonDocument.Parse(await reader.ReadToEndAsync()).RootElement.Clone();
        return (zip, manifest, folder);
    }

    private static IReadOnlyList<string> Paths(JsonElement manifest)
        => [.. manifest.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("path").GetString()!)];

    [Fact]
    public async Task Bundle_TakesNewestSessionsAndLogDays_UnderOneFolder_WithManifestHashes()
    {
        Session("20260920-080000");
        Session("20260921-080000");
        Session("20260922-080000");
        Log("ProsimCompanion-20260920.log");
        Log("ProsimCompanion-20260921.log");
        Log("ProsimCompanion-20260922.log");
        Log("ProsimCompanion-20260922_001.log");
        Log("ProsimCompanion-wire-20260922.log", bytes: 5000);
        File.WriteAllText(Settings, """{ "prosim": { "host": "sim-pc", "apiKey": "secret-value-here" } }""");

        var (built, manifest, folder) = await BuildAsync(Request(sessions: 2, days: 2));
        using var _ = built;

        Assert.All(built.Entries, e => Assert.StartsWith(folder + "/", e.FullName, StringComparison.Ordinal));
        var paths = Paths(manifest);
        Assert.Contains("sessions/session-20260922-080000.jsonl", paths);
        Assert.Contains("sessions/session-20260921-080000.jsonl", paths);
        Assert.DoesNotContain("sessions/session-20260920-080000.jsonl", paths);
        Assert.Contains("logs/ProsimCompanion-20260922.log", paths);
        Assert.Contains("logs/ProsimCompanion-20260922_001.log", paths);
        Assert.Contains("logs/ProsimCompanion-20260921.log", paths);
        Assert.DoesNotContain("logs/ProsimCompanion-20260920.log", paths);
        Assert.DoesNotContain("logs/ProsimCompanion-wire-20260922.log", paths);
        Assert.Contains("config/settings.json", paths);
        Assert.Contains("versions.txt", paths);

        Assert.Equal(1, manifest.GetProperty("bundleVersion").GetInt32());
        Assert.Equal("0.5.0-rc.3", manifest.GetProperty("appVersion").GetString());
        Assert.Equal("abc1234", manifest.GetProperty("commit").GetString());
        Assert.Empty(manifest.GetProperty("truncated").EnumerateArray());

        // Every listed hash matches the bytes actually in the zip.
        foreach (var file in manifest.GetProperty("files").EnumerateArray())
        {
            var entry = built.GetEntry($"{folder}/{file.GetProperty("path").GetString()}");
            Assert.NotNull(entry);
            using var stream = entry.Open();
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            Assert.Equal(file.GetProperty("bytes").GetInt64(), memory.Length);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(memory.ToArray())), file.GetProperty("sha256").GetString());
        }
    }

    [Fact]
    public async Task Bundle_RedactsSettings_AndWritesVersionsWithDependencies()
    {
        File.WriteAllText(Settings, """{ "prosim": { "host": "sim-pc", "apiKey": "secret-value-here" }, "webUi": { "accessToken": "0123456789ABCDEF0123456789ABCDEF" } }""");

        var (zip, _, folder) = await BuildAsync(Request());
        using (zip)
        {
            using var settings = new StreamReader(zip.GetEntry($"{folder}/config/settings.json")!.Open());
            var text = await settings.ReadToEndAsync();
            Assert.DoesNotContain("secret-value-here", text, StringComparison.Ordinal);
            Assert.DoesNotContain("0123456789ABCDEF", text, StringComparison.Ordinal);
            Assert.Contains("\"host\": \"sim-pc\"", text, StringComparison.Ordinal);

            using var versions = new StreamReader(zip.GetEntry($"{folder}/versions.txt")!.Open());
            var banner = await versions.ReadToEndAsync();
            Assert.Contains("ProsimCompanion 0.5.0-rc.3 (abc1234)", banner, StringComparison.Ordinal);
            Assert.Contains("ProSim: Connected", banner, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Bundle_IncludesWireLogsOnlyWhenAsked()
    {
        Log("ProsimCompanion-20260922.log");
        Log("ProsimCompanion-wire-20260922.log");

        var (zip, manifest, _) = await BuildAsync(Request(wire: true));
        zip.Dispose();

        Assert.Contains("logs/ProsimCompanion-wire-20260922.log", Paths(manifest));
    }

    [Fact]
    public async Task Bundle_MissingDirectoriesAndSettings_DegradeToAnEmptyValidBundle()
    {
        var (zip, manifest, folder) = await BuildAsync(Request());
        using (zip)
        {
            var paths = Paths(manifest);
            Assert.Equal(["versions.txt"], paths);
            Assert.NotNull(zip.GetEntry($"{folder}/manifest.json"));
        }
    }

    [Fact]
    public void Select_OverCap_DropsOldestSessionsFirst_ThenOldestLogs_AndRecordsThem()
    {
        Session("20260920-080000", bytes: 100);
        Session("20260921-080000", bytes: 100);
        Session("20260922-080000", bytes: 100);
        Log("ProsimCompanion-20260921.log", bytes: 100);
        Log("ProsimCompanion-20260922.log", bytes: 100);

        // 500 bytes selected; a 250-byte cap must drop two oldest sessions (300 left), then
        // the oldest log (200 left).
        var selection = DiagnosticsBundleBuilder.Select(Request(cap: 250));

        Assert.Equal(
            ["sessions/session-20260922-080000.jsonl", "logs/ProsimCompanion-20260922.log"],
            selection.Included.Select(i => i.RelativePath).ToList());
        Assert.Equal(
            ["sessions/session-20260920-080000.jsonl", "sessions/session-20260921-080000.jsonl", "logs/ProsimCompanion-20260921.log"],
            selection.Truncated.Select(t => t.Path).ToList());
        Assert.All(selection.Truncated, t => Assert.Equal(100, t.Bytes));
    }

    [Fact]
    public void Select_ClampsCountsToTheAllowedRange()
    {
        for (var day = 1; day <= 25; day++)
        {
            Session($"202609{day:00}-080000");
        }

        var selection = DiagnosticsBundleBuilder.Select(Request(sessions: 99, days: 99));

        Assert.Equal(20, selection.Included.Count(i => i.RelativePath.StartsWith("sessions/", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Bundle_ReadsAFileAnotherWriterStillHoldsOpen()
    {
        Directory.CreateDirectory(Sessions);
        var live = Path.Combine(Sessions, "session-20260926-100000.jsonl");
        await using var writer = new FileStream(live, FileMode.Create, FileAccess.Write, FileShare.Read);
        writer.Write("{\"type\":\"session-started\"}\n"u8);
        writer.Flush();

        var (zip, manifest, _) = await BuildAsync(Request());
        zip.Dispose();

        var entry = Assert.Single(manifest.GetProperty("files").EnumerateArray(), f => f.GetProperty("path").GetString()!.StartsWith("sessions/", StringComparison.Ordinal));
        Assert.Equal(27, entry.GetProperty("bytes").GetInt64());
    }
}
