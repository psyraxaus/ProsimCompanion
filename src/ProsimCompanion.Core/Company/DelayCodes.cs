using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProsimCompanion.Core.Company;

/// <summary>One IATA delay code the captain may pick (issue #185), from <c>delay-codes.json</c>.</summary>
/// <param name="Code">The two-digit IATA code ("15").</param>
/// <param name="Title">The short title the card and the FO use ("Boarding").</param>
/// <param name="Spoken">Words the pilot may say instead of the number ("boarding", "late boarding").</param>
public sealed record DelayCodeEntry(string Code, string Title, IReadOnlyList<string> Spoken);

/// <summary>
/// The delay-code list (issue #185): the shipped IATA subset, user-editable in
/// <c>%LOCALAPPDATA%\ProsimCompanion\config\delay-codes.json</c> (seeded like
/// <c>atc-requests.json</c>). Matching is by number ("delay code 15", "one five") or by the
/// entry's spoken aliases as whole words. Code 99 is the "other" every timeout falls to.
/// </summary>
public sealed class DelayCodeCatalog
{
    /// <summary>The code an unanswered question is recorded under.</summary>
    public const string OtherCode = "99";

    /// <summary>The shipped list — what <c>delay-codes.json</c> carries at install.</summary>
    public static IReadOnlyList<DelayCodeEntry> Defaults { get; } =
    [
        new("11", "Late passengers", ["late passengers", "passengers", "late pax", "pax"]),
        new("15", "Boarding", ["boarding", "slow boarding", "late boarding"]),
        new("31", "Ground handling", ["ground handling", "handling", "loading", "baggage"]),
        new("33", "Refuelling", ["refuelling", "refueling", "fuel", "fuelling"]),
        new("41", "Technical defect", ["technical", "tech", "defect", "technical defect", "maintenance"]),
        new("71", "Weather at departure", ["weather", "departure weather", "wx"]),
        new("75", "De-icing", ["de-icing", "deicing", "de ice", "deice"]),
        new("81", "ATC flow", ["atc flow", "flow", "flow control", "slot"]),
        new("89", "ATC restriction", ["atc restriction", "atc", "restriction", "air traffic control"]),
        new("93", "Late inbound aircraft", ["late inbound", "inbound", "late aircraft", "aircraft late"]),
        new(OtherCode, "Other", ["other", "miscellaneous"]),
    ];

    private IReadOnlyList<DelayCodeEntry> _entries = Defaults;

    /// <summary>The current list.</summary>
    public IReadOnlyList<DelayCodeEntry> Entries => _entries;

    /// <summary>Replaces the list from the file's JSON (<c>{"codes":[{code,title,spoken:[]}]}</c>);
    /// an unreadable or empty file keeps the shipped defaults and returns false.</summary>
    public bool Load(string json)
    {
        var parsed = Parse(json);
        if (parsed.Count == 0)
        {
            return false;
        }

        _entries = parsed;
        return true;
    }

    /// <summary>Pure parse; empty on any problem.</summary>
    public static IReadOnlyList<DelayCodeEntry> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            var file = JsonSerializer.Deserialize<DelayCodesFile>(json, Options);
            return file?.Codes is { Count: > 0 } codes
                ? codes.Where(c => !string.IsNullOrWhiteSpace(c.Code)).Select(c => new DelayCodeEntry(c.Code!.Trim(), (c.Title ?? "").Trim(), c.Spoken ?? [])).ToList()
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The shipped file text.</summary>
    public static string DefaultsJson()
        => JsonSerializer.Serialize(new DelayCodesFile
        {
            Comment = "IATA delay codes the First Officer offers at off-blocks when the flight is late (issue #185). Edit freely: code, title, and the words you may say instead of the number. 99 is the code an unanswered question falls to.",
            Codes = Defaults.Select(d => new DelayCodeJson { Code = d.Code, Title = d.Title, Spoken = [.. d.Spoken] }).ToList(),
        }, Indented);

    /// <summary>The entry a pilot's words name: the two-digit number anywhere in the words, else
    /// an alias as a whole phrase inside them (longest alias first). Null when nothing matches.</summary>
    public static DelayCodeEntry? Match(string normalizedUtterance, IReadOnlyList<DelayCodeEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(normalizedUtterance);
        ArgumentNullException.ThrowIfNull(entries);
        var words = normalizedUtterance.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var number = words.FirstOrDefault(w => w.Length == 2 && w.All(char.IsAsciiDigit));
        if (number is not null && entries.FirstOrDefault(e => e.Code == number) is { } byNumber)
        {
            return byNumber;
        }

        var padded = " " + normalizedUtterance + " ";
        return entries
            .SelectMany(e => e.Spoken.Select(alias => (Entry: e, Alias: Speech.NatoPhonetics.SpeakIdentifier(alias) == alias ? alias.ToLowerInvariant() : alias.ToLowerInvariant())))
            .Where(x => x.Alias.Length > 0 && padded.Contains(" " + Normalize(x.Alias) + " ", StringComparison.Ordinal))
            .OrderByDescending(x => x.Alias.Length)
            .Select(x => x.Entry)
            .FirstOrDefault();
    }

    private static string Normalize(string text)
        => string.Join(' ', text.ToLowerInvariant().Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries));

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions Indented = new(Options) { WriteIndented = true };

    private sealed class DelayCodesFile
    {
        [JsonPropertyName("//")]
        public string? Comment { get; set; }

        [JsonPropertyName("codes")]
        public List<DelayCodeJson>? Codes { get; set; }
    }

    private sealed class DelayCodeJson
    {
        [JsonPropertyName("code")]
        public string? Code { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("spoken")]
        public List<string>? Spoken { get; set; }
    }
}
