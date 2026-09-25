using System.Globalization;
using System.Text.RegularExpressions;

namespace ProsimCompanion.Core.Logging;

/// <summary>One parsed CMTrace line (the inverse of <see cref="CmTraceFormat.Line"/>).</summary>
public sealed record CmTraceEntry(
    DateTimeOffset Timestamp,
    string Message,
    string Component,
    string Source,
    int Severity,
    int ThreadId)
{
    /// <summary>Serilog-style level name for the CMTrace severity.</summary>
    public string Level => Severity switch
    {
        CmTraceFormat.SeverityWarning => "Warning",
        CmTraceFormat.SeverityError => "Error",
        _ => "Information",
    };
}

/// <summary>
/// Reads the app's CMTrace log files back into entries — the support reducer's fallback when
/// a bundle comes from a build that predates the session-log mirror (or has it switched
/// off). Lives in Core next to the writer so the two formats can never drift apart: the
/// regex here is the one the flight-verification workflow quotes.
/// </summary>
public static partial class CmTraceParser
{
    /// <summary>Parses every well-formed line; a torn or foreign line is skipped, never thrown on.
    /// Multi-line messages (exception text) are supported: a record runs to its closing tag.</summary>
    public static IReadOnlyList<CmTraceEntry> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var entries = new List<CmTraceEntry>();
        foreach (Match match in LinePattern().Matches(text))
        {
            if (TryBuild(match, out var entry))
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    /// <summary>Parses a file opened share-read-write (the app may still be writing it).</summary>
    public static IReadOnlyList<CmTraceEntry> ParseFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    private static bool TryBuild(Match match, out CmTraceEntry entry)
    {
        entry = null!;
        // time="HH:mm:ss.fff+600" (offset in minutes, sign always present for >= 0), date="MM-dd-yyyy"
        var time = match.Groups["time"].Value;
        var date = match.Groups["date"].Value;
        var signAt = time.LastIndexOfAny(['+', '-']);
        if (signAt < 0)
        {
            return false;
        }

        if (!int.TryParse(time[signAt..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var offsetMinutes)
            || !DateTime.TryParseExact(
                $"{date} {time[..signAt]}", "MM-dd-yyyy HH:mm:ss.fff",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
            || !int.TryParse(match.Groups["type"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var severity))
        {
            return false;
        }

        _ = int.TryParse(match.Groups["thread"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var thread);
        entry = new CmTraceEntry(
            new DateTimeOffset(local, TimeSpan.FromMinutes(offsetMinutes)),
            match.Groups["msg"].Value,
            match.Groups["component"].Value,
            match.Groups["file"].Value,
            severity,
            thread);
        return true;
    }

    [GeneratedRegex(
        @"<!\[LOG\[(?<msg>.*?)\]LOG\]!><time=""(?<time>[^""]*)"" date=""(?<date>[^""]*)"" component=""(?<component>[^""]*)"" context=""[^""]*"" type=""(?<type>\d)"" thread=""(?<thread>\d*)"" file=""(?<file>[^""]*)"">",
        RegexOptions.Singleline)]
    private static partial Regex LinePattern();
}
