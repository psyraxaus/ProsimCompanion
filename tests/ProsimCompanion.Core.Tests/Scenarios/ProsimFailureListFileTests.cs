using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.Core.Diagnostics;
using ProsimCompanion.Core.Scenarios;
using ProsimCompanion.Reduce;
using Xunit;

namespace ProsimCompanion.Core.Tests.Scenarios;

/// <summary>
/// Issue #197: ProSim's live failure list reaches a file (<c>prosim-failures.json</c>) and the
/// diagnostics bundle, so the scenario catalogue's names can be mapped offline — the shape of
/// the document, the overwrite, the bundle's manifest entry, and a reducer that takes a
/// bundle with the file and only counts it.
/// </summary>
public sealed class ProsimFailureListFileTests : IDisposable
{
    private static readonly DateTimeOffset Listed = new(2026, 11, 14, 4, 0, 12, TimeSpan.Zero);
    private readonly string _dir = Directory.CreateTempSubdirectory("pc-failures-").FullName;
    private readonly List<string> _zips = [];

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
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private ProsimFailureListFile File_() => new(NullLogger<ProsimFailureListFile>.Instance, Path.Combine(_dir, "data", ProsimFailureListFile.FileName));

    private static ProsimFailureInfo[] TwoFailures() =>
    [
        new("Fuel pump left 1", "Left tank pump 1 fails.", "Fuel", "Fuel pump", "Left 1", "Minor"),
        new("Blue hydraulic low level", " ", "Hydraulic"),
    ];

    [Fact]
    public void Build_KeepsProSimsOrder_DropsNamelessEntries_AndBlanksBecomeNull()
    {
        var document = ProsimFailureListFile.Build(
            [.. TwoFailures(), new ProsimFailureInfo("  "), new ProsimFailureInfo("")], " 1.76b5 ", Listed.ToOffset(TimeSpan.FromHours(11)));

        Assert.Equal(2, document.Count);
        Assert.Equal(["Fuel pump left 1", "Blue hydraulic low level"], document.Failures.Select(f => f.Name));
        Assert.Null(document.Failures[1].Description);
        Assert.Equal("Hydraulic", document.Failures[1].Domain);
        Assert.Equal("1.76b5", document.ProsimVersion);
        Assert.Equal(Listed, document.ListedUtc);
        Assert.Equal(TimeSpan.Zero, document.ListedUtc.Offset);
    }

    [Fact]
    public void Serialize_HasTheStampAndVersionAtTheTop_ThenOneObjectPerFailure()
    {
        var json = ProsimFailureListFile.Serialize(ProsimFailureListFile.Build(TwoFailures(), "1.76b5", Listed));

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(["listedUtc", "prosimVersion", "count", "failures"], root.EnumerateObject().Select(p => p.Name));
        Assert.Equal(Listed, root.GetProperty("listedUtc").GetDateTimeOffset());
        Assert.Equal("1.76b5", root.GetProperty("prosimVersion").GetString());
        Assert.Equal(2, root.GetProperty("count").GetInt32());

        var first = root.GetProperty("failures")[0];
        Assert.Equal("Fuel pump left 1", first.GetProperty("name").GetString());
        Assert.Equal("Left tank pump 1 fails.", first.GetProperty("description").GetString());
        Assert.Equal("Fuel", first.GetProperty("domain").GetString());
        Assert.Equal("Fuel pump", first.GetProperty("groupName").GetString());
        Assert.Equal("Left 1", first.GetProperty("groupMember").GetString());
        Assert.Equal("Minor", first.GetProperty("failureClass").GetString());

        // What ProSim did not say is left out, not written as null.
        var second = root.GetProperty("failures")[1];
        Assert.Equal(["name", "domain"], second.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void Serialize_WithoutAVersion_LeavesItOut()
    {
        using var doc = JsonDocument.Parse(ProsimFailureListFile.Serialize(ProsimFailureListFile.Build(TwoFailures(), null, Listed)));
        Assert.False(doc.RootElement.TryGetProperty("prosimVersion", out _));
    }

    [Fact]
    public void Write_CreatesTheFolder_AndEachReadOverwritesTheLast()
    {
        var file = File_();
        Assert.True(file.Write(ProsimFailureListFile.Build(TwoFailures(), "1.76b5", Listed)));
        Assert.True(file.Write(ProsimFailureListFile.Build([new ProsimFailureInfo("APU fire")], "1.76b6", Listed.AddHours(1))));

        using var doc = JsonDocument.Parse(File.ReadAllText(file.Path));
        Assert.Equal(1, doc.RootElement.GetProperty("count").GetInt32());
        Assert.Equal("APU fire", doc.RootElement.GetProperty("failures")[0].GetProperty("name").GetString());
        Assert.Equal("1.76b6", doc.RootElement.GetProperty("prosimVersion").GetString());
        // No temp file is left beside it.
        Assert.Equal([ProsimFailureListFile.FileName], Directory.GetFiles(Path.GetDirectoryName(file.Path)!).Select(Path.GetFileName));
    }

    [Fact]
    public void DefaultPath_IsUnderTheAppsDataRoot()
    {
        var file = new ProsimFailureListFile(NullLogger<ProsimFailureListFile>.Instance);
        Assert.Equal(Path.Combine(Core.Configuration.UserDataPaths.Root, "prosim-failures.json"), file.Path);
    }

    [Fact]
    public async Task Bundle_CarriesTheList_InTheManifest_AndTheReducerOnlyCountsIt()
    {
        var file = File_();
        file.Write(ProsimFailureListFile.Build(TwoFailures(), "1.76b5", Listed));
        var created = new DateTimeOffset(2026, 11, 14, 8, 0, 0, TimeSpan.Zero);
        var request = new DiagnosticsBundleRequest(
            Path.Combine(_dir, "sessions"), Path.Combine(_dir, "logs"), null, AppBuildInfo.Create("1.0.0-rc.6+abc", "net", "os", "X64"))
        {
            CreatedUtc = created,
            FailureListPath = file.Path,
        };
        var zipPath = await new DiagnosticsBundleBuilder(NullLogger<DiagnosticsBundleBuilder>.Instance).BuildAsync(request, CancellationToken.None);
        _zips.Add(zipPath);

        using (var zip = ZipFile.OpenRead(zipPath))
        {
            var folder = DiagnosticsBundleBuilder.FolderName(created);
            var entry = zip.GetEntry($"{folder}/prosim-failures.json");
            Assert.NotNull(entry);
            using var reader = new StreamReader(entry.Open());
            Assert.Equal(File.ReadAllText(file.Path), await reader.ReadToEndAsync()); // the file as it is on disk
        }

        // The reader's validation (extension allow-list, manifest hashes) takes it; it is not a
        // session, not a log and not the health snapshot.
        using var bundle = BundleReader.Open(zipPath);
        Assert.True(bundle.HashesVerified);
        Assert.Contains(bundle.Manifest!.Files, f => f.Path == "prosim-failures.json" && f.Bytes > 0);
        Assert.NotNull(bundle.FailureListFile);
        Assert.Empty(bundle.SessionFiles);
        Assert.Empty(bundle.LogFiles);
        Assert.Null(bundle.HealthSnapshotFile);

        var report = Reducer.Run(bundle, ProbeEvaluator.LoadCatalog(ProbeEvaluator.EmbeddedCatalogJson()), maxKb: 400);
        Assert.Contains("prosim-failures.json: 2 ProSim failure(s) listed (ProSim 1.76b5) — not reduced, read the file", report.Bundle.Notes);
        Assert.Null(report.Health);
    }

    [Fact]
    public async Task Bundle_WithoutTheFile_IsAsBefore()
    {
        var request = new DiagnosticsBundleRequest(
            Path.Combine(_dir, "sessions"), Path.Combine(_dir, "logs"), null, AppBuildInfo.Create("1.0.0-rc.6+abc", "net", "os", "X64"))
        {
            FailureListPath = Path.Combine(_dir, "data", "prosim-failures.json"), // never written
        };
        var zipPath = await new DiagnosticsBundleBuilder(NullLogger<DiagnosticsBundleBuilder>.Instance).BuildAsync(request, CancellationToken.None);
        _zips.Add(zipPath);

        using var bundle = BundleReader.Open(zipPath);
        Assert.Null(bundle.FailureListFile);
        Assert.DoesNotContain(bundle.Manifest!.Files, f => f.Path.Contains("prosim-failures", StringComparison.Ordinal));
        var report = Reducer.Run(bundle, ProbeEvaluator.LoadCatalog(ProbeEvaluator.EmbeddedCatalogJson()), maxKb: 400);
        Assert.DoesNotContain(report.Bundle.Notes, n => n.Contains("prosim-failures", StringComparison.Ordinal));
    }

    [Fact]
    public void Reducer_AnUnreadableList_IsANote_NotARefusal()
    {
        var folder = Path.Combine(_dir, "bare");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "prosim-failures.json"), "{ not json");

        using var bundle = BundleReader.Open(folder);
        Assert.NotNull(bundle.FailureListFile);
        var report = Reducer.Run(bundle, ProbeEvaluator.LoadCatalog(ProbeEvaluator.EmbeddedCatalogJson()), maxKb: 400);
        Assert.Contains(report.Bundle.Notes, n => n.StartsWith("prosim-failures.json: unreadable", StringComparison.Ordinal));
    }
}
