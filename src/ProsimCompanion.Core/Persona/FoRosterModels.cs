using System.Text.Json;

namespace ProsimCompanion.Core.Persona;

/// <summary>
/// One First Officer of the roster (<c>fo-roster.json</c>, issue #194). The four persona
/// axes plus confidence describe how they SPEAK; the hours and base are cosmetic — the FO may
/// say them, nothing operational ever reads them. <see cref="Voice"/> is a provider-prefixed
/// voice id (<c>kokoro:af_sky</c>, <c>elevenlabs:&lt;id&gt;</c>); null keeps the configured
/// First Officer voice.
/// </summary>
public sealed record FoRosterEntry(
    string Id,
    string Name,
    string Experience,
    string Formality,
    int Chattiness,
    string Confidence,
    int HoursOnType,
    string? Base,
    string? Voice)
{
    public static readonly IReadOnlyList<string> Experiences = ["junior", "standard", "senior"];
    public static readonly IReadOnlyList<string> Formalities = ["casual", "standard", "formal"];
    public static readonly IReadOnlyList<string> Confidences = ["nervous", "steady", "assured"];

    /// <summary>The hours as the FO says them: rounded to the nearest hundred, 0 for none.</summary>
    public int SpokenHours => (int)(Math.Round(HoursOnType / 100.0, MidpointRounding.AwayFromZero) * 100);
}

/// <summary>
/// The persisted draw (<c>fo-roster-state.json</c>): which FO is on duty, what the draw was
/// for (a duty-day id or a sim calendar day), whether the gate intro has been spoken for it,
/// and the pin. Kept outside <c>settings.json</c> so a settings page save can never clobber a
/// draw made after the page loaded.
/// </summary>
public sealed class FoRosterState
{
    /// <summary>The drawn (or pinned) FO's id; null before the first draw.</summary>
    public string? CurrentId { get; set; }

    /// <summary>The id drawn before <see cref="CurrentId"/> — excluded from the next draw.</summary>
    public string? PreviousId { get; set; }

    /// <summary>What the current draw covers: <c>day:&lt;dayId&gt;</c> (company day mode) or
    /// <c>sim:yyyy-MM-dd</c> (the sim's local calendar day). The next trigger with a different
    /// stamp draws again; the same stamp (a restart mid-day) keeps the FO.</summary>
    public string? DrawnFor { get; set; }

    /// <summary>The gate intro has been spoken for the current draw.</summary>
    public bool IntroDone { get; set; }

    /// <summary>The pinned FO's id (pinned mode); null when nobody is pinned.</summary>
    public string? PinnedId { get; set; }
}

/// <summary>Pure parsing of <c>fo-roster.json</c>: an object with an <c>officers</c> array (or
/// a bare array). Every entry is normalised (axes lower-cased and clamped to the known values,
/// chattiness 0–3, hours ≥ 0); an entry without an id or a name is skipped and named in the
/// returned problems so the Config page can show it.</summary>
public static class FoRosterFile
{
    public sealed record ParseResult(IReadOnlyList<FoRosterEntry> Officers, IReadOnlyList<string> Problems);

    public static ParseResult Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(
            json,
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

        var root = document.RootElement;
        JsonElement array;
        if (root.ValueKind == JsonValueKind.Array)
        {
            array = root;
        }
        else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("officers", out var officers)
            && officers.ValueKind == JsonValueKind.Array)
        {
            array = officers;
        }
        else
        {
            return new([], ["no \"officers\" array"]);
        }

        var entries = new List<FoRosterEntry>();
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var item in array.EnumerateArray())
        {
            index++;
            if (item.ValueKind != JsonValueKind.Object)
            {
                problems.Add($"entry {index}: not an object");
                continue;
            }

            var id = Text(item, "id");
            var name = Text(item, "name");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            {
                problems.Add($"entry {index}: id and name are required");
                continue;
            }

            if (!seen.Add(id))
            {
                problems.Add($"entry {index}: duplicate id \"{id}\"");
                continue;
            }

            entries.Add(new FoRosterEntry(
                id.Trim(),
                name.Trim(),
                Axis(Text(item, "experience"), FoRosterEntry.Experiences, "standard"),
                Axis(Text(item, "formality"), FoRosterEntry.Formalities, "standard"),
                Math.Clamp(Number(item, "chattiness") ?? 1, 0, 3),
                Axis(Text(item, "confidence"), FoRosterEntry.Confidences, "steady"),
                Math.Max(0, Number(item, "hoursOnType") ?? 0),
                NullIfBlank(Text(item, "base"))?.ToUpperInvariant(),
                NullIfBlank(Text(item, "voice"))));
        }

        return new(entries, problems);
    }

    private static string? Text(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? Number(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? (int)Math.Round(number)
            : null;

    private static string Axis(string? value, IReadOnlyList<string> allowed, string fallback)
    {
        var text = value?.Trim().ToLowerInvariant();
        return text is not null && allowed.Contains(text) ? text : fallback;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
