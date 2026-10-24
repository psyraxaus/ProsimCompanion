using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProsimCompanion.Core.Day;

/// <summary>One row of the maximum-FDP table: a report-time band and the maximum per sector count.</summary>
/// <param name="FromMinutes">Band start, minutes after midnight (inclusive).</param>
/// <param name="ToMinutes">Band end, minutes after midnight (inclusive); a band may wrap midnight.</param>
/// <param name="MaxFdpMinutes">Index 0 = 1–2 sectors, 1 = 3, 2 = 4 … 8 = 10 or more.</param>
public sealed record FtlRow(int FromMinutes, int ToMinutes, IReadOnlyList<int> MaxFdpMinutes)
{
    public bool Contains(int minutesOfDay)
        => FromMinutes <= ToMinutes
            ? minutesOfDay >= FromMinutes && minutesOfDay <= ToMinutes
            : minutesOfDay >= FromMinutes || minutesOfDay <= ToMinutes;
}

/// <summary>
/// The basic maximum daily FDP table (issue #189) — EASA ORO.FTL.205(b)(1) Table 2 for an
/// acclimatised crew member, encoded as data in <c>config/ftl-easa.json</c> (user-editable) with
/// this copy as the shipped default. Report times are taken in UTC here (no acclimatisation
/// model); extensions, split duty, in-flight rest and the night-duty rules are ignored —
/// the table is advisory and says so wherever it is shown.
/// </summary>
public sealed class FtlTable
{
    public const string Name = "EASA ORO.FTL.205 basic maximum daily FDP (acclimatised), UTC report time, no extensions";

    private static readonly IReadOnlyList<FtlRow> DefaultRows =
    [
        Row("06:00", "13:29", "13:00", "12:30", "12:00", "11:30", "11:00", "10:30", "10:00", "09:30", "09:00"),
        Row("13:30", "13:59", "12:45", "12:15", "11:45", "11:15", "10:45", "10:15", "09:45", "09:15", "09:00"),
        Row("14:00", "14:29", "12:30", "12:00", "11:30", "11:00", "10:30", "10:00", "09:30", "09:00", "09:00"),
        Row("14:30", "14:59", "12:15", "11:45", "11:15", "10:45", "10:15", "09:45", "09:15", "09:00", "09:00"),
        Row("15:00", "15:29", "12:00", "11:30", "11:00", "10:30", "10:00", "09:30", "09:00", "09:00", "09:00"),
        Row("15:30", "15:59", "11:45", "11:15", "10:45", "10:15", "09:45", "09:15", "09:00", "09:00", "09:00"),
        Row("16:00", "16:29", "11:30", "11:00", "10:30", "10:00", "09:30", "09:00", "09:00", "09:00", "09:00"),
        Row("16:30", "16:59", "11:15", "10:45", "10:15", "09:45", "09:15", "09:00", "09:00", "09:00", "09:00"),
        Row("17:00", "04:59", "11:00", "10:30", "10:00", "09:30", "09:00", "09:00", "09:00", "09:00", "09:00"),
        Row("05:00", "05:14", "12:00", "11:30", "11:00", "10:30", "10:00", "09:30", "09:00", "09:00", "09:00"),
        Row("05:15", "05:29", "12:15", "11:45", "11:15", "10:45", "10:15", "09:45", "09:15", "09:00", "09:00"),
        Row("05:30", "05:44", "12:30", "12:00", "11:30", "11:00", "10:30", "10:00", "09:30", "09:00", "09:00"),
        Row("05:45", "05:59", "12:45", "12:15", "11:45", "11:15", "10:45", "10:15", "09:45", "09:15", "09:00"),
    ];

    private FtlTable(IReadOnlyList<FtlRow> rows, string source)
    {
        Rows = rows;
        Source = source;
    }

    public IReadOnlyList<FtlRow> Rows { get; }

    /// <summary>"shipped" or the file the rows came from.</summary>
    public string Source { get; }

    public static FtlTable Default { get; } = new(DefaultRows, "shipped");

    /// <summary>The user file's rows; null when the JSON is unreadable or has no rows (the caller keeps the default).</summary>
    public static FtlTable? Parse(string? json, string source = "file")
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var file = JsonSerializer.Deserialize<FtlFile>(json, Options);
            if (file?.Rows is not { Count: > 0 } rows)
            {
                return null;
            }

            var parsed = new List<FtlRow>();
            foreach (var row in rows)
            {
                if (row.From is null || row.To is null || row.MaxFdp is not { Count: 9 })
                {
                    return null;
                }

                parsed.Add(new FtlRow(Minutes(row.From), Minutes(row.To), row.MaxFdp.Select(Minutes).ToList()));
            }

            return new FtlTable(parsed, source);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The shipped file text (the seed).</summary>
    public static string DefaultJson()
        => JsonSerializer.Serialize(new FtlFile
        {
            Comment = Name + ". Rows: report-time band (UTC, inclusive, may wrap midnight) and the maximum FDP for 1-2, 3, 4, 5, 6, 7, 8, 9 and 10+ sectors as h:mm. Edit for your operator's scheme; acclimatisation, extensions, split duty and in-flight rest are NOT modelled. Verify against the current regulation before relying on it.",
            Rows = DefaultRows.Select(r => new FtlRowFile { From = Clock(r.FromMinutes), To = Clock(r.ToMinutes), MaxFdp = r.MaxFdpMinutes.Select(Clock).ToList() }).ToList(),
        }, Indented);

    /// <summary>Maximum FDP for a report at <paramref name="reportUtc"/> with <paramref name="sectors"/> sectors; null when no band covers the time.</summary>
    public TimeSpan? MaxFdp(DateTimeOffset reportUtc, int sectors)
    {
        var minutesOfDay = reportUtc.Hour * 60 + reportUtc.Minute;
        var row = Rows.FirstOrDefault(r => r.Contains(minutesOfDay));
        if (row is null)
        {
            return null;
        }

        var index = Math.Clamp(sectors <= 2 ? 0 : sectors - 2, 0, row.MaxFdpMinutes.Count - 1);
        return TimeSpan.FromMinutes(row.MaxFdpMinutes[index]);
    }

    private static FtlRow Row(string from, string to, params string[] maxFdp)
        => new(Minutes(from), Minutes(to), maxFdp.Select(Minutes).ToList());

    private static int Minutes(string clock)
    {
        var parts = clock.Trim().Split(':');
        if (parts.Length != 2)
        {
            throw new FormatException($"'{clock}' is not h:mm");
        }

        return int.Parse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture) * 60 + int.Parse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture);
    }

    private static string Clock(int minutes) => $"{minutes / 60:00}:{minutes % 60:00}";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions Indented = new(Options) { WriteIndented = true };

    private sealed class FtlFile
    {
        [JsonPropertyName("//")]
        public string? Comment { get; set; }

        [JsonPropertyName("rows")]
        public List<FtlRowFile>? Rows { get; set; }
    }

    private sealed class FtlRowFile
    {
        [JsonPropertyName("from")]
        public string? From { get; set; }

        [JsonPropertyName("to")]
        public string? To { get; set; }

        [JsonPropertyName("maxFdp")]
        public List<string>? MaxFdp { get; set; }
    }
}
