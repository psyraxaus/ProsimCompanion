using Microsoft.Extensions.Logging;

namespace ProsimCompanion.Core.Diagnostics.Ecam;

/// <summary>
/// One tick of the diagnostics pipeline, with no timer: run every collector, merge the pages,
/// fold the cautions, stamp the memo, read the event ring. Testable without a host. The only
/// state it keeps is what a single tick cannot know: which collectors are mid-failure (so a
/// failing collector logs once per streak, not once per second) and which cautions were up
/// last tick (so a caution's raised time survives and RECENT gets its raise and clear lines).
/// </summary>
public sealed class DiagnosticsSnapshotCore
{
    private readonly ILogger _logger;
    private readonly HashSet<IDiagnosticsCollector> _failing = new(ReferenceEqualityComparer.Instance);
    private readonly StateTransitions<string> _cautionEdges = new();

    public DiagnosticsSnapshotCore(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>Builds the snapshot for <paramref name="now"/>.</summary>
    /// <param name="now">The tick instant; every event and caution of this tick carries it.</param>
    /// <param name="collectors">Every registered collector, any page, any order.</param>
    /// <param name="memoSource">The strip facts no collector owns.</param>
    /// <param name="eventLog">The RECENT ring, pushed to by collectors and by this core.</param>
    /// <param name="previous">Last tick's snapshot (caution raise times carry over).</param>
    public DiagnosticsSnapshot ProcessTick(
        DateTimeOffset now,
        IReadOnlyList<IDiagnosticsCollector> collectors,
        IDiagMemoSource memoSource,
        DiagEventLog eventLog,
        DiagnosticsSnapshot? previous)
    {
        ArgumentNullException.ThrowIfNull(collectors);
        ArgumentNullException.ThrowIfNull(memoSource);
        ArgumentNullException.ThrowIfNull(eventLog);

        var events = new DiagEventSink(eventLog, now);
        var cautions = new DiagCautionSink(now);
        var pages = new Dictionary<string, DiagPageBuilder>(StringComparer.Ordinal);
        foreach (var id in DiagPageIds.Drawing)
        {
            pages[id] = new DiagPageBuilder(id, events);
        }

        foreach (var collector in collectors)
        {
            var name = collector.GetType().Name;
            var pageId = collector.Page;
            if (!pages.TryGetValue(pageId, out var page))
            {
                if (_failing.Add(collector))
                {
                    _logger.LogWarning(
                        "Diagnostics collector {Collector} names page {Page}, which does not exist; it is ignored",
                        name, pageId);
                }

                continue;
            }

            var scratch = new DiagPageBuilder(pageId, events);
            try
            {
                collector.Contribute(scratch, cautions);
                if (_failing.Remove(collector))
                {
                    _logger.LogInformation("Diagnostics collector {Collector} is reporting again", name);
                    events.Push($"{pageId.ToUpperInvariant()} COLLECTOR OK");
                }
            }
            catch (Exception ex)
            {
                scratch.DowngradeToNoData();
                if (_failing.Add(collector))
                {
                    _logger.LogWarning(ex,
                        "Diagnostics collector {Collector} threw; its nodes show XX until it recovers (logged once per failure streak)",
                        name);
                    events.Push($"{pageId.ToUpperInvariant()} COLLECTOR FAULT", DiagState.Caution);
                }
            }

            page.Merge(scratch);
        }

        var folded = FoldCautions(cautions.Cautions, previous?.Cautions, events);
        var warnings = folded.Count(c => c.Severity == DiagState.Warning);
        var cautionCount = folded.Count(c => c.Severity == DiagState.Caution) + warnings;

        var memo = memoSource.Read(now) with
        {
            CautionCount = cautionCount,
            WarningCount = warnings,
        };

        // PANELS exists only while its collector drew something (the Skalarki link is on);
        // an empty page means "hidden", not "all XX".
        var panels = pages[DiagPageIds.Panels].Build();
        if (panels.Nodes.Count == 0 && panels.Readouts.Count == 0 && panels.CautionLine is null)
        {
            panels = null;
        }

        return new DiagnosticsSnapshot(
            now,
            memo,
            pages[DiagPageIds.Voice].Build(),
            pages[DiagPageIds.Sim].Build(),
            pages[DiagPageIds.Remote].Build(),
            pages[DiagPageIds.Gsx].Build(),
            folded,
            [.. cautions.InopSystems],
            [.. cautions.DegradedSystems],
            eventLog.Snapshot(),
            panels);
    }

    /// <summary>Keeps a caution's first-raised time across ticks and pushes a RECENT line on
    /// the edges: raised (amber or red) and cleared (green). Warnings sort before cautions,
    /// then by raise time, so the STATUS page reads top-down in urgency.</summary>
    private IReadOnlyList<DiagCaution> FoldCautions(
        IReadOnlyList<DiagCaution> current,
        IReadOnlyList<DiagCaution>? previous,
        DiagEventSink events)
    {
        var carried = new List<DiagCaution>(current.Count);
        foreach (var caution in current)
        {
            var before = previous?.FirstOrDefault(p => p.System == caution.System && p.Title == caution.Title);
            carried.Add(before is null ? caution : caution with { RaisedUtc = before.RaisedUtc });
        }

        var states = new Dictionary<string, DiagState>(StringComparer.Ordinal);
        foreach (var caution in carried)
        {
            states[$"{caution.System} {caution.Title}"] = caution.Severity;
        }

        if (previous is not null)
        {
            foreach (var old in previous)
            {
                states.TryAdd($"{old.System} {old.Title}", DiagState.Normal);
            }
        }

        foreach (var (id, _, to) in _cautionEdges.Observe(states))
        {
            events.Push(id, to);
        }

        return
        [
            .. carried
                .OrderByDescending(c => c.Severity.Rank())
                .ThenBy(c => c.RaisedUtc),
        ];
    }
}
