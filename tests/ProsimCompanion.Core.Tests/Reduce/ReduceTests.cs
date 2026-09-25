using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.Core.Diagnostics;
using ProsimCompanion.Core.Logging;
using ProsimCompanion.Reduce;
using Xunit;

namespace ProsimCompanion.Core.Tests.Reduce;

/// <summary>
/// The support reducer (tools/ProsimCompanion.Reduce): the report SHAPE over an existing
/// recording (values that depend on the sample are not pinned), the bundle validation that
/// refuses unsafe input, probe verdict semantics, and the size-budget trimming order.
/// </summary>
public sealed class ReduceTests : IDisposable
{
    private static readonly string Recordings = Path.Combine(AppContext.BaseDirectory, "Flight", "Recordings");
    private static readonly string[] Verdicts = ["pass", "fail", "untested"];

    private readonly string _dir = Directory.CreateTempSubdirectory("pc-reduce-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static IReadOnlyList<ProbeDefinition> Catalog() => ProbeEvaluator.LoadCatalog(ProbeEvaluator.EmbeddedCatalogJson());

    [Fact]
    public void Report_OverTheRecordingsFolder_HasTheDocumentedShape()
    {
        using var bundle = BundleReader.Open(Recordings);
        var report = Reducer.Run(bundle, Catalog(), maxKb: 400);
        var json = JsonSerializer.Serialize(report, ReduceReport.Json);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("folder", root.GetProperty("bundle").GetProperty("source").GetString());
        Assert.False(root.GetProperty("bundle").GetProperty("hashesVerified").GetBoolean());
        Assert.True(root.GetProperty("sessions").GetArrayLength() >= 1);

        var session = root.GetProperty("sessions").EnumerateArray()
            .First(s => s.GetProperty("file").GetString()!.StartsWith("essa-egll", StringComparison.Ordinal));
        foreach (var key in new[] { "file", "firstEvent", "lastEvent", "eventCount", "endedCleanly", "header", "facts", "phaseTimeline", "samples", "eventsByType", "eventsByTypePerPhase", "logEvents", "cmTraceEvents", "gaps", "logSource" })
        {
            Assert.True(session.TryGetProperty(key, out _), $"session.{key} missing");
        }

        // A pre-0.5.0 recording has no header payload: the build canary says so explicitly.
        Assert.Equal("unversioned", session.GetProperty("header").GetProperty("status").GetString());
        Assert.True(session.GetProperty("phaseTimeline").GetArrayLength() > 0);
        var edge = session.GetProperty("phaseTimeline")[0];
        Assert.True(edge.TryGetProperty("from", out _) && edge.TryGetProperty("to", out _) && edge.TryGetProperty("at", out _));

        var samples = session.GetProperty("samples");
        Assert.True(samples.GetProperty("rawSamples").GetInt32() > samples.GetProperty("points").GetArrayLength());
        Assert.True(samples.GetProperty("points").GetArrayLength() > 0);
        Assert.True(samples.GetProperty("alt").GetProperty("max").GetDouble() > samples.GetProperty("alt").GetProperty("min").GetDouble());
        Assert.True(session.GetProperty("facts").TryGetProperty("blockMinutes", out _));
        Assert.True(session.GetProperty("eventsByType").GetProperty("flight-sample").GetInt32() > 100);
        Assert.Equal("none", session.GetProperty("logSource").GetString());

        Assert.True(root.GetProperty("probes").EnumerateObject().Any());
        foreach (var probe in root.GetProperty("probes").EnumerateObject())
        {
            Assert.Contains(probe.Value.GetProperty("verdict").GetString(), Verdicts);
            Assert.True(probe.Value.GetProperty("evidence").GetArrayLength() > 0);
        }

        Assert.True(root.GetProperty("forLlm").GetArrayLength() > 50);
        var first = root.GetProperty("forLlm")[0];
        Assert.True(first.TryGetProperty("checks", out var checks) && checks.GetArrayLength() > 0);
    }

    [Fact]
    public void Report_NeverEmitsRawSamples()
    {
        using var bundle = BundleReader.Open(Recordings);
        var report = Reducer.Run(bundle, Catalog(), maxKb: 400);
        var json = JsonSerializer.Serialize(report, ReduceReport.Json);

        // The raw flight-sample keys (short names) do not appear as JSON properties anywhere.
        Assert.DoesNotContain("\"engRaw\":", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"pbRaw\":", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Trim_DropsHistogramsFirst_ThenThinsPoints_AndNotesTheStaticRemainder()
    {
        using var bundle = BundleReader.Open(Recordings);
        var full = Reducer.Run(bundle, Catalog(), maxKb: 4000);
        var trimmed = Reducer.Trim(full, maxKb: 1);

        Assert.All(trimmed.Sessions, s => Assert.Empty(s.EventsByTypePerPhase));
        Assert.All(trimmed.Sessions, s => Assert.True(s.EventsByType.Count <= 20));
        Assert.True(trimmed.Sessions.Sum(s => s.Samples.Points.Count) < full.Sessions.Sum(s => s.Samples.Points.Count));
        Assert.Contains(trimmed.Bundle.Notes, n => n.StartsWith("over budget after trimming", StringComparison.Ordinal));
        // The probe texts are untouched by contract.
        Assert.Equal(full.ForLlm.Count, trimmed.ForLlm.Count);
    }

    [Fact]
    public async Task Zip_FromTheBundleBuilder_OpensAndVerifies_AndATamperedFileIsRefused()
    {
        var sessions = Path.Combine(_dir, "sessions");
        Directory.CreateDirectory(sessions);
        File.Copy(Path.Combine(Recordings, "synthetic-full-flight.jsonl"), Path.Combine(sessions, "session-20260829-090000.jsonl"));
        var builder = new DiagnosticsBundleBuilder(NullLogger<DiagnosticsBundleBuilder>.Instance);
        var zip = await builder.BuildAsync(
            new DiagnosticsBundleRequest(sessions, Path.Combine(_dir, "logs"), null, AppBuildInfo.Create("0.5.0+abc", "net", "os", "X64")),
            CancellationToken.None);
        try
        {
            using (var bundle = BundleReader.Open(zip))
            {
                Assert.Equal("zip", bundle.Source);
                Assert.True(bundle.HashesVerified);
                Assert.Single(bundle.SessionFiles);
                var report = Reducer.Run(bundle, Catalog());
                Assert.Equal("0.5.0", report.Bundle.AppVersion);
                Assert.Equal("abc", report.Bundle.Commit);
            }

            // Tamper: append a byte to the session file inside the zip.
            var tampered = Path.Combine(_dir, "tampered.zip");
            File.Copy(zip, tampered);
            using (var archive = ZipFile.Open(tampered, ZipArchiveMode.Update))
            {
                var entry = archive.Entries.First(e => e.FullName.EndsWith(".jsonl", StringComparison.Ordinal));
                using var stream = entry.Open();
                stream.Seek(0, SeekOrigin.End);
                stream.WriteByte((byte)'\n');
            }

            var ex = Assert.Throws<BundleRejectedException>(() => BundleReader.Open(tampered));
            Assert.Contains("SHA-256 mismatch", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(zip);
        }
    }

    [Theory]
    [InlineData("../escape.jsonl", "escapes")]
    [InlineData("bundle/evil.exe", "not a .jsonl")]
    public void Zip_UnsafeEntries_AreRefusedBeforeExtraction(string entryName, string reason)
    {
        var zip = Path.Combine(_dir, "unsafe.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry(entryName);
            using var stream = entry.Open();
            stream.WriteByte((byte)'x');
        }

        var ex = Assert.Throws<BundleRejectedException>(() => BundleReader.Open(zip));
        Assert.Contains(reason, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Zip_TooManyEntries_IsRefused()
    {
        var zip = Path.Combine(_dir, "many.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            for (var i = 0; i <= BundleReader.MaxEntries; i++)
            {
                archive.CreateEntry($"b/f{i}.txt");
            }
        }

        var ex = Assert.Throws<BundleRejectedException>(() => BundleReader.Open(zip));
        Assert.Contains("entries", ex.Message, StringComparison.Ordinal);
    }

    private LoadedSession Session(string name, params string[] lines)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllLines(path, lines);
        return LoadedSession.Load(path);
    }

    private static string Line(string at, string type, string payload = "null")
        => $"{{\"timestamp\":\"{at}\",\"type\":\"{type}\",\"payload\":{payload}}}";

    [Fact]
    public void Probes_SignatureSemantics_AbsentPresentUntested()
    {
        var catalog = ProbeEvaluator.LoadCatalog("""
            { "probes": [
              { "id": "absent-ok", "issue": 1, "state": "open", "kind": "assert-absent", "sources": ["session"], "checks": ["x"],
                "machine": { "kind": "signature", "signature": { "source": "session", "type": "gsx-decision", "contains": "boom" } } },
              { "id": "absent-fail", "issue": 2, "state": "open", "kind": "assert-absent", "sources": ["session"], "checks": ["x"],
                "machine": { "kind": "signature", "signature": { "source": "session", "contains": "\"reason\":\"bad\"" } } },
              { "id": "present-untested", "issue": 3, "state": "open", "kind": "assert-present", "sources": ["log"], "checks": ["x"],
                "machine": { "kind": "signature", "trigger": { "source": "log", "contains": "never" }, "signature": { "source": "log", "contains": "whatever" } } },
              { "id": "present-pass", "issue": 4, "state": "open", "kind": "assert-present", "sources": ["log"], "checks": ["x"],
                "machine": { "kind": "signature", "trigger": { "source": "log", "contains": "PTT down" }, "signature": { "source": "log", "contains": "ASR heard" } } },
              { "id": "present-fail", "issue": 5, "state": "open", "kind": "assert-present", "sources": ["log"], "checks": ["x"],
                "machine": { "kind": "signature", "trigger": { "source": "log", "contains": "PTT down" }, "signature": { "source": "log", "contains": "nothing like this" } } },
              { "id": "judgement", "issue": 6, "state": "open", "kind": "assert-absent", "sources": ["session"], "checks": ["needs a human"], "notes": "n" }
            ] }
            """);
        var session = Session("session-a.jsonl",
            Line("2026-09-26T10:00:00Z", "session-started"),
            Line("2026-09-26T10:00:01Z", "gsx-decision", "{\"reason\":\"bad\"}"),
            Line("2026-09-26T10:00:02Z", "log.warning", "{\"component\":\"Ptt\",\"message\":\"FO PTT down\"}"),
            Line("2026-09-26T10:00:03Z", "log.warning", "{\"component\":\"Router\",\"message\":\"ASR heard 'gear up'\"}"),
            Line("2026-09-26T10:00:04Z", "session-ended"));

        var (results, forLlm) = ProbeEvaluator.Evaluate(catalog, [session], []);

        Assert.Equal("pass", results["absent-ok"].Verdict);
        Assert.Equal("fail", results["absent-fail"].Verdict);
        Assert.Contains("\"reason\":\"bad\"", results["absent-fail"].Evidence[0], StringComparison.Ordinal);
        Assert.Equal("untested", results["present-untested"].Verdict);
        Assert.Equal("pass", results["present-pass"].Verdict);
        Assert.Equal("fail", results["present-fail"].Verdict);
        var judgement = Assert.Single(forLlm);
        Assert.Equal("judgement", judgement.Id);
        Assert.Equal(["needs a human"], judgement.Checks);
        Assert.Equal("n", judgement.Notes);
    }

    [Fact]
    public void Probes_LogSignatures_PreferCmTraceWhenPresent()
    {
        var catalog = ProbeEvaluator.LoadCatalog("""
            { "probes": [ { "id": "p", "issue": 1, "state": "open", "kind": "assert-absent", "sources": ["log"], "checks": ["x"],
              "machine": { "kind": "signature", "signature": { "source": "log", "contains": "Joystick poll pass took", "minCount": 2 } } } ] }
            """);
        var session = Session("session-b.jsonl",
            Line("2026-09-26T10:00:00Z", "session-started"),
            Line("2026-09-26T10:00:02Z", "log.warning", "{\"component\":\"Ptt\",\"message\":\"Joystick poll pass took 900 ms\"}"));
        var cmTrace = CmTraceParser.Parse(
            CmTraceFormat.Line(new DateTimeOffset(2026, 9, 26, 10, 0, 2, TimeSpan.Zero), "Joystick poll pass took 900 ms", "Ptt", "P.Ptt", 2, 1) + "\n"
            + CmTraceFormat.Line(new DateTimeOffset(2026, 9, 26, 10, 0, 3, TimeSpan.Zero), "Joystick poll pass took 950 ms", "Ptt", "P.Ptt", 2, 1) + "\n");

        var (withCm, _) = ProbeEvaluator.Evaluate(catalog, [session], cmTrace);
        var (mirrorOnly, _) = ProbeEvaluator.Evaluate(catalog, [session], []);

        Assert.Equal("fail", withCm["p"].Verdict);      // two CMTrace hits reach the threshold
        Assert.Equal("pass", mirrorOnly["p"].Verdict);  // one mirrored hit does not
    }

    [Fact]
    public void Session_HeaderRestartsGapsAndClusters()
    {
        var a = Session("session-20260926-100000.jsonl",
            Line("2026-09-26T10:00:00Z", "session-started", "{\"appVersion\":\"0.5.0-rc.3\",\"commit\":\"abc\",\"runtime\":\".NET\",\"os\":\"win\",\"architecture\":\"X64\",\"sampleIntervalSeconds\":1,\"activeProfile\":null}"),
            Line("2026-09-26T10:00:01Z", "phase-changed", "{\"previous\":\"Unknown\",\"current\":\"Cruise\",\"rule\":\"r\",\"reason\":\"why\"}"),
            Line("2026-09-26T10:00:02Z", "flight-sample", "{\"ph\":\"Cruise\",\"alt\":30000,\"ias\":250,\"vs\":0,\"ra\":2500,\"gs\":440}"),
            Line("2026-09-26T10:00:03Z", "log.error", "{\"component\":\"Gsx\",\"message\":\"Gate 12 refused (attempt 3)\",\"exceptionType\":\"System.IO.IOException\"}"),
            Line("2026-09-26T10:00:04Z", "log.error", "{\"component\":\"Gsx\",\"message\":\"Gate 47 refused (attempt 9)\",\"exceptionType\":\"System.IO.IOException\"}"),
            Line("2026-09-26T10:01:00Z", "flight-sample", "{\"ph\":\"Cruise\",\"alt\":31000,\"ias\":255,\"vs\":100,\"ra\":2500,\"gs\":445}"));
        var b = Session("session-20260926-110000.jsonl",
            Line("2026-09-26T11:00:00Z", "session-started"),
            Line("2026-09-26T11:00:01Z", "session-ended"));

        var contents = new BundleContents("folder", _dir, null, null, [a.Path, b.Path], [], false, []);
        var report = Reducer.Run(contents, [], maxKb: 400);

        var first = report.Sessions[0];
        Assert.Equal("session-started", first.Header.Status);
        Assert.Equal("0.5.0-rc.3", first.Header.AppVersion);
        Assert.False(first.EndedCleanly);
        Assert.Single(first.PhaseTimeline);
        Assert.Equal("Cruise", first.PhaseTimeline[0].To);
        var cluster = Assert.Single(first.LogEvents);
        Assert.Equal(2, cluster.Count);
        Assert.Equal("Gate # refused (attempt #)", cluster.Pattern);
        Assert.Equal("Gate 12 refused (attempt 3)", cluster.Example);
        Assert.Equal("System.IO.IOException", cluster.ExceptionType);
        Assert.Equal("Cruise", cluster.Phase);
        Assert.Equal("session", first.LogSource);
        var gap = Assert.Single(first.Gaps);
        Assert.Equal(56, gap.Seconds);
        Assert.Equal(2, first.Samples.RawSamples);
        Assert.Equal(31000, first.Samples.Alt!.Max);

        Assert.Equal("unversioned", report.Sessions[1].Header.Status);
        Assert.True(report.Sessions[1].EndedCleanly);
        var restart = Assert.Single(report.Restarts);
        Assert.Equal(first.File, restart.PreviousFile);
        Assert.Equal("flight-sample", restart.PreviousLastType);
    }

    [Fact]
    public void LogClusterer_NormalizesDigitsAndGuids()
    {
        Assert.Equal("Request # failed after #s", LogClusterer.Normalize("Request 8f1c2d3e-1111-2222-3333-444455556666 failed after 2.5s"));
        Assert.Equal("Only the first line", LogClusterer.Normalize("Only the first line\r\n   at Some.Frame()"));
    }
}
