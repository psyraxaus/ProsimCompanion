using System.Text.RegularExpressions;

namespace ProsimCompanion.Reduce;

/// <summary>One warning/error occurrence from either stream, before clustering.</summary>
public sealed record LogOccurrence(
    DateTimeOffset At,
    string Level,
    string Component,
    string Message,
    string? ExceptionType,
    string? Phase);

/// <summary>
/// Groups warnings/errors by (component, message shape): digits, GUIDs and opaque ids
/// (Blazor circuit ids, Kestrel connection/request ids — long letter+digit tokens) collapse
/// to '#' so "Gate 12 refused" and "Gate 47 refused" are one problem with a count, not two
/// lines the LLM has to correlate. The first verbatim message is kept as the example.
/// </summary>
public static partial class LogClusterer
{
    /// <summary>The normalized message key.</summary>
    public static string Normalize(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var firstLine = message.Split('\n', 2)[0].TrimEnd('\r');
        var noGuids = GuidPattern().Replace(firstLine, "#");
        // Opaque ids before the digit pass: a circuit id such as ivYGm0OeM3IGmKn3VJlagBnjJ5
        // would otherwise keep its letters and split one crash into a cluster per circuit
        // (2026-09-28 bundle: six CircuitHost + three Kestrel rows for the one /speech fault).
        var noIds = OpaqueIdPattern().Replace(noGuids, "#");
        return DigitsPattern().Replace(noIds, "#");
    }

    public static IReadOnlyList<LogCluster> Cluster(IEnumerable<LogOccurrence> occurrences)
    {
        ArgumentNullException.ThrowIfNull(occurrences);
        var clusters = new Dictionary<(string Level, string Component, string Pattern), Accumulator>();
        var order = new List<(string, string, string)>();
        foreach (var occurrence in occurrences.OrderBy(o => o.At))
        {
            var key = (occurrence.Level, occurrence.Component, Normalize(occurrence.Message));
            if (!clusters.TryGetValue(key, out var acc))
            {
                acc = new Accumulator(occurrence);
                clusters[key] = acc;
                order.Add(key);
            }

            acc.Add(occurrence);
        }

        return [.. order.Select(key => clusters[key].ToCluster(key.Item3))];
    }

    private sealed class Accumulator(LogOccurrence first)
    {
        private int _count;
        private DateTimeOffset _last = first.At;

        public void Add(LogOccurrence occurrence)
        {
            _count++;
            if (occurrence.At > _last)
            {
                _last = occurrence.At;
            }
        }

        public LogCluster ToCluster(string pattern) => new(
            first.Level,
            first.Component,
            pattern,
            _count,
            first.At,
            _last,
            first.Phase,
            Truncate(first.Message, 300),
            first.ExceptionType);
    }

    private static string Truncate(string text, int max)
    {
        var firstLine = text.Split('\n', 2)[0].TrimEnd('\r');
        return firstLine.Length <= max ? firstLine : firstLine[..max] + "…";
    }

    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex GuidPattern();

    [GeneratedRegex(@"\d+(\.\d+)?")]
    private static partial Regex DigitsPattern();

    /// <summary>A token of 12+ url-safe characters that mixes letters and digits (with an
    /// optional ":hex" suffix — Kestrel request ids). Airport codes, service names and
    /// dataref paths never match: they are shorter, or letters only, or contain dots.</summary>
    [GeneratedRegex(@"(?<![\w\-])(?=[A-Za-z0-9_\-]*\d)(?=[A-Za-z0-9_\-]*[A-Za-z])[A-Za-z0-9_\-]{12,}(?::[0-9A-Fa-f]{4,})?(?![\w\-])")]
    private static partial Regex OpaqueIdPattern();
}
