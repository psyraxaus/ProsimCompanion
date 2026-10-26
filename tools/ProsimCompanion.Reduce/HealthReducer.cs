using System.Text.Json;
using ProsimCompanion.Core.Diagnostics.Ecam;

namespace ProsimCompanion.Reduce;

/// <summary>
/// Reads the two optional diagnostics files into the report's <c>health</c> section. Both are
/// optional (a bundle from before the pages has neither), and a file that does not parse is a
/// note, never a refusal: the sessions and logs are the primary evidence and must still reduce.
/// </summary>
public static class HealthReducer
{
    /// <summary>At most this many RECENT lines are carried (the ring holds 50; the document
    /// budget wants fewer).</summary>
    public const int MaxRecent = 24;

    public static HealthSummary? Reduce(string? snapshotPath, string? eventsPath)
    {
        if (snapshotPath is null && eventsPath is null)
        {
            return null;
        }

        var notes = new List<string>();
        var cautions = new List<HealthCaution>();
        var inop = new List<string>();
        var degraded = new List<string>();
        var abnormal = new Dictionary<string, string>(StringComparer.Ordinal);
        DateTimeOffset? at = null;

        if (snapshotPath is not null)
        {
            try
            {
                var snapshot = JsonSerializer.Deserialize<DiagnosticsSnapshot>(File.ReadAllText(snapshotPath), EcamJson.Options);
                if (snapshot is null)
                {
                    notes.Add("diagnostics-snapshot.json: empty document");
                }
                else
                {
                    at = snapshot.AtUtc == DateTimeOffset.MinValue ? null : snapshot.AtUtc;
                    cautions.AddRange(snapshot.Cautions.Select(c => new HealthCaution(
                        c.System, c.Title, c.Severity.ToString(), [.. c.Actions.Select(a => $"{a.Label} {a.Value}")], c.RaisedUtc)));
                    inop.AddRange(snapshot.InopSystems);
                    degraded.AddRange(snapshot.Degraded);
                    foreach (var page in new[] { snapshot.Voice, snapshot.Sim, snapshot.Remote, snapshot.Gsx })
                    {
                        foreach (var node in page.Nodes.Values.Where(n => n.State is not DiagState.Normal and not DiagState.NotApplicable))
                        {
                            abnormal[$"{page.Id}.{node.Id}"] = node.Detail is null
                                ? $"{node.State} {node.Value}"
                                : $"{node.State} {node.Value} ({node.Detail})";
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                notes.Add($"diagnostics-snapshot.json: unreadable ({ex.Message})");
            }
        }

        var recent = new List<HealthEvent>();
        if (eventsPath is not null)
        {
            try
            {
                foreach (var line in File.ReadLines(eventsPath))
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    try
                    {
                        var evt = JsonSerializer.Deserialize<DiagEvent>(line, EcamJson.Options);
                        if (evt is not null)
                        {
                            recent.Add(new HealthEvent(evt.AtUtc, evt.Text, evt.State.ToString()));
                        }
                    }
                    catch (JsonException)
                    {
                        notes.Add("diagnostics-events.jsonl: a line did not parse and was skipped");
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                notes.Add($"diagnostics-events.jsonl: unreadable ({ex.Message})");
            }
        }

        if (recent.Count > MaxRecent)
        {
            recent.RemoveRange(0, recent.Count - MaxRecent);
            notes.Add($"recent: oldest lines dropped, newest {MaxRecent} kept");
        }

        return new HealthSummary(cautions, inop, degraded, at, abnormal, recent, notes.Distinct(StringComparer.Ordinal).ToList());
    }
}
