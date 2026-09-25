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
/// Groups warnings/errors by (component, message shape): digits and GUIDs collapse to '#' so
/// "Gate 12 refused" and "Gate 47 refused" are one problem with a count, not two lines the
/// LLM has to correlate. The first verbatim message is kept as the example.
/// </summary>
public static partial class LogClusterer
{
    /// <summary>The normalized message key.</summary>
    public static string Normalize(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var firstLine = message.Split('\n', 2)[0].TrimEnd('\r');
        var noGuids = GuidPattern().Replace(firstLine, "#");
        return DigitsPattern().Replace(noGuids, "#");
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
}
