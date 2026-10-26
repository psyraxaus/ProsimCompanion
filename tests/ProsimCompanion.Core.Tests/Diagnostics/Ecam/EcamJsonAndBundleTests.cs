using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.App.Hosting;
using ProsimCompanion.Core.Diagnostics;
using ProsimCompanion.Core.Diagnostics.Ecam;
using ProsimCompanion.Reduce;
using Xunit;

namespace ProsimCompanion.Core.Tests.Diagnostics.Ecam;

/// <summary>The JSON the endpoint serves (enums by name), the two bundle files, and the
/// reducer's health section with and without them.</summary>
public sealed class EcamJsonAndBundleTests : IDisposable
{
    private static readonly IAppBuildInfo Build = AppBuildInfo.Create("0.8.0-rc.10+abc1234", ".NET 10.0.1", "Windows 10", "X64");
    private readonly string _root = Directory.CreateTempSubdirectory("pc-ecam-").FullName;
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
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static DiagnosticsSnapshot Populated()
    {
        var log = new DiagEventLog();
        var core = new DiagnosticsSnapshotCore(new CountingLogger());
        IDiagnosticsCollector[] collectors =
        [
            new DelegateCollector(DiagPageIds.Voice, (p, c) =>
            {
                p.Node("stt", DiagState.Normal, "412 ms", "LARGE-V3", "MAC MINI");
                p.Node("llm", DiagState.Caution, "XX", "NO RESP", "RTX 3090");
                p.Link("stt", "llm");
                p.Gauge("e2e", 1840, 0, 5000, 2500, 4000, "MS");
                p.Readout("vram", "18.2");
                p.CautionLine("LLM HOST SLEEPING · WOL SENT");
                c.Raise("VOICE", "LLM HOST NOT RESPONDING", DiagState.Caution, new DiagAction("WOL MAGIC PKT", "SENT 00:42"));
                c.Inop("LLM BRIEFING");
                p.Events.Push("WOL SENT 10.0.1.23");
            }),
        ];
        return core.ProcessTick(EcamTestSupport.T0, collectors, new FixedMemoSource(), log, null);
    }

    [Fact]
    public void SnapshotJson_SerialisesEnumsAsNames_AndRoundTrips()
    {
        var json = DiagnosticsEndpoints.SnapshotJson(Populated());
        using var doc = JsonDocument.Parse(json);
        var voice = doc.RootElement.GetProperty("voice");
        Assert.Equal("Caution", voice.GetProperty("nodes").GetProperty("llm").GetProperty("state").GetString());
        Assert.Equal("Caution", voice.GetProperty("links")[0].GetProperty("state").GetString());
        Assert.Equal("Caution", doc.RootElement.GetProperty("cautions")[0].GetProperty("severity").GetString());
        Assert.DoesNotContain("\"state\":1", json, StringComparison.Ordinal);

        var back = JsonSerializer.Deserialize<DiagnosticsSnapshot>(json, EcamJson.Options);
        Assert.NotNull(back);
        Assert.Equal(DiagState.Caution, back.Voice.Nodes["llm"].State);
        Assert.Equal(1840, back.Voice.Gauges["e2e"].Value);
        Assert.Equal(["LLM BRIEFING"], back.InopSystems);
    }

    [Fact]
    public void EmptySnapshot_Serialises()
    {
        var json = EcamJson.Serialize(DiagnosticsSnapshot.Empty);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(0, doc.RootElement.GetProperty("memo").GetProperty("cautionCount").GetInt32());
    }

    [Fact]
    public async Task Bundle_ContainsTheTwoDiagnosticsFiles_AndTheReducerReadsThem()
    {
        var snapshot = Populated();
        var request = new DiagnosticsBundleRequest(Path.Combine(_root, "sessions"), Path.Combine(_root, "logs"), null, Build)
        {
            CreatedUtc = EcamTestSupport.T0,
            DiagnosticsSnapshot = snapshot,
            DiagnosticsEvents = snapshot.Recent,
        };
        var builder = new DiagnosticsBundleBuilder(NullLogger<DiagnosticsBundleBuilder>.Instance);
        var zipPath = await builder.BuildAsync(request, CancellationToken.None);
        _zips.Add(zipPath);

        using (var zip = ZipFile.OpenRead(zipPath))
        {
            var folder = DiagnosticsBundleBuilder.FolderName(EcamTestSupport.T0);
            Assert.NotNull(zip.GetEntry($"{folder}/{DiagnosticsBundleBuilder.SnapshotFileName}"));
            var events = zip.GetEntry($"{folder}/{DiagnosticsBundleBuilder.EventsFileName}");
            Assert.NotNull(events);
            using var reader = new StreamReader(events.Open());
            var lines = (await reader.ReadToEndAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(snapshot.Recent.Count, lines.Length);
            Assert.Contains("WOL SENT", lines[0], StringComparison.Ordinal); // oldest first in the file
        }

        using var bundle = BundleReader.Open(zipPath);
        Assert.Empty(bundle.SessionFiles);
        Assert.NotNull(bundle.HealthSnapshotFile);
        Assert.NotNull(bundle.HealthEventsFile);

        var report = Reducer.Run(bundle, ProbeEvaluator.LoadCatalog(ProbeEvaluator.EmbeddedCatalogJson()), maxKb: 400);
        Assert.NotNull(report.Health);
        Assert.Equal("LLM HOST NOT RESPONDING", Assert.Single(report.Health.Cautions).Title);
        Assert.Equal(["LLM BRIEFING"], report.Health.Inop);
        Assert.Equal(EcamTestSupport.T0, report.Health.LastSnapshotUtc);
        Assert.Contains("voice.llm", report.Health.AbnormalNodes.Keys);
        Assert.DoesNotContain("voice.stt", report.Health.AbnormalNodes.Keys);
        Assert.NotEmpty(report.Health.Recent);

        var json = JsonSerializer.Serialize(report, ReduceReport.Json);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("health", out _));
    }

    [Fact]
    public async Task Bundle_WithoutTheFiles_ReducesWithNoHealthSection()
    {
        var request = new DiagnosticsBundleRequest(Path.Combine(_root, "sessions"), Path.Combine(_root, "logs"), null, Build)
        {
            CreatedUtc = EcamTestSupport.T0,
        };
        var builder = new DiagnosticsBundleBuilder(NullLogger<DiagnosticsBundleBuilder>.Instance);
        var zipPath = await builder.BuildAsync(request, CancellationToken.None);
        _zips.Add(zipPath);

        using var bundle = BundleReader.Open(zipPath);
        Assert.Null(bundle.HealthSnapshotFile);
        var report = Reducer.Run(bundle, ProbeEvaluator.LoadCatalog(ProbeEvaluator.EmbeddedCatalogJson()), maxKb: 400);
        Assert.Null(report.Health);

        var json = JsonSerializer.Serialize(report, ReduceReport.Json);
        Assert.DoesNotContain("\"health\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void HealthReducer_UnreadableSnapshot_IsANoteNotAFailure()
    {
        var path = Path.Combine(_root, DiagnosticsBundleBuilder.SnapshotFileName);
        File.WriteAllText(path, "{ not json");
        var health = HealthReducer.Reduce(path, null);
        Assert.NotNull(health);
        Assert.Empty(health.Cautions);
        Assert.Contains(health.Notes, n => n.StartsWith("diagnostics-snapshot.json: unreadable", StringComparison.Ordinal));
    }
}
