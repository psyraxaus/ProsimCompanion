using System.Text.Json;
using ProsimCompanion.Core.Logging;

namespace ProsimCompanion.Reduce;

/// <summary>
/// The whole pipeline over an opened bundle: load sessions, parse CMTrace logs, reduce each
/// session, detect restarts, evaluate the probes, and trim the document to the size budget.
/// Pure over <see cref="BundleContents"/> so tests can run it over a recording folder.
/// </summary>
public static class Reducer
{
    /// <summary>Default size budget: the LLM step's context.</summary>
    public const int DefaultMaxKb = 60;

    public static ReduceReport Run(BundleContents bundle, IReadOnlyList<ProbeDefinition> catalog, int maxKb = DefaultMaxKb)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(catalog);

        var sessions = bundle.SessionFiles.Select(LoadedSession.Load)
            .OrderBy(s => s.FirstEvent ?? DateTimeOffset.MaxValue)
            .ThenBy(s => s.File, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var cmTrace = new List<CmTraceEntry>();
        var notes = new List<string>(bundle.Notes);
        foreach (var log in bundle.LogFiles)
        {
            try
            {
                cmTrace.AddRange(CmTraceParser.ParseFile(log));
            }
            catch (IOException ex)
            {
                notes.Add($"{Path.GetFileName(log)}: unreadable ({ex.Message})");
            }
        }

        cmTrace.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));

        var reports = sessions.Select(s => SessionReducer.Reduce(s, cmTrace)).ToList();

        // CMTrace warnings/errors that no session's time range covers (app running with no
        // session file, or clock skew) — clustered so they are not lost.
        var unattributed = LogClusterer.Cluster(cmTrace
            .Where(e => e.Severity >= CmTraceFormat.SeverityWarning && !sessions.Any(s => SessionReducer.Covers(s, e.Timestamp)))
            .Select(e => new LogOccurrence(e.Timestamp.ToUniversalTime(), e.Level, e.Component, e.Message, null, null)));

        var restarts = new List<RestartReport>();
        for (var i = 1; i < sessions.Count; i++)
        {
            if (!reports[i - 1].EndedCleanly)
            {
                var timed = sessions[i - 1].Timed;
                var previousLast = timed.Count > 0 ? timed[^1] : null;
                restarts.Add(new RestartReport(
                    sessions[i - 1].File, sessions[i].File, previousLast?.At, previousLast?.Type, sessions[i].FirstEvent));
            }
        }

        var (probes, forLlm) = ProbeEvaluator.Evaluate(catalog, sessions, cmTrace);

        var summary = new BundleSummary(
            bundle.Source,
            bundle.Folder,
            bundle.Manifest?.CreatedUtc,
            bundle.Manifest?.AppVersion,
            bundle.Manifest?.Commit,
            bundle.Manifest?.Files.Count ?? bundle.SessionFiles.Count + bundle.LogFiles.Count,
            bundle.SessionFiles.Count,
            bundle.LogFiles.Count,
            bundle.HashesVerified,
            [.. bundle.Manifest?.Truncated.Select(t => $"{t.Path} ({t.Bytes} bytes)") ?? []],
            notes);

        var report = new ReduceReport(summary, reports, restarts, unattributed, probes, forLlm);
        return Trim(report, maxKb);
    }

    /// <summary>Serialized size in bytes.</summary>
    public static int SizeOf(ReduceReport report)
        => JsonSerializer.SerializeToUtf8Bytes(report, ReduceReport.Json).Length;

    /// <summary>
    /// Keeps the document under <paramref name="maxKb"/>: histograms go first (per-phase, then
    /// the by-type map collapsed to its 20 largest), then the sample points are thinned by half
    /// until it fits or nothing more can go. The facts, timeline, clusters and probes stay.
    /// </summary>
    public static ReduceReport Trim(ReduceReport report, int maxKb)
    {
        ArgumentNullException.ThrowIfNull(report);
        var budget = Math.Max(1, maxKb) * 1024;
        if (SizeOf(report) <= budget)
        {
            return report;
        }

        var sessions = report.Sessions.ToList();
        var notes = report.Bundle.Notes.ToList();

        // Step 1: per-phase histograms.
        sessions = [.. sessions.Select(s => s with { EventsByTypePerPhase = new Dictionary<string, IReadOnlyDictionary<string, int>>() })];
        notes.Add("trimmed: eventsByTypePerPhase removed to fit the size budget");
        report = report with { Sessions = sessions, Bundle = report.Bundle with { Notes = notes } };
        if (SizeOf(report) <= budget)
        {
            return report;
        }

        // Step 2: by-type histograms to the top 20.
        sessions = [.. sessions.Select(s => s with
        {
            EventsByType = s.EventsByType.OrderByDescending(kv => kv.Value).Take(20).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
        })];
        notes.Add("trimmed: eventsByType limited to the 20 most frequent types");
        report = report with { Sessions = sessions, Bundle = report.Bundle with { Notes = notes } };
        if (SizeOf(report) <= budget)
        {
            return report;
        }

        // Step 3: thin the sample points until it fits or they are gone.
        for (var round = 0; round < 12 && SizeOf(report) > budget; round++)
        {
            if (sessions.All(s => s.Samples.Points.Count == 0))
            {
                break;
            }

            sessions = [.. sessions.Select(s => s with
            {
                Samples = s.Samples with { Points = [.. s.Samples.Points.Where((_, i) => i % 2 == 0)] },
            })];
            report = report with { Sessions = sessions };
        }

        notes.Add("trimmed: flight-sample points thinned to fit the size budget");
        report = report with { Bundle = report.Bundle with { Notes = notes } };

        // The probe texts under forLlm are passed through untouched by contract; when they
        // alone exceed the budget the pipeline must pass a smaller catalog via --probes.
        if (SizeOf(report) > budget)
        {
            var staticKb = SizeOf(report with { ForLlm = [] });
            notes.Add($"over budget after trimming: {(SizeOf(report) - staticKb) / 1024} KB of the document is forLlm probe text (use --probes with a smaller catalog or raise --max-kb)");
            report = report with { Bundle = report.Bundle with { Notes = notes } };
        }

        return report;
    }
}
