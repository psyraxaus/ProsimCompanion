using System.Text.Json;
using System.Text.RegularExpressions;

namespace ProsimCompanion.Core.Speech;

/// <summary>
/// How the voice says an abbreviation (owner request 2026-11-14: "ECAM, not E, C, A, M" —
/// likewise SID and STAR). Two lists: <see cref="Words"/> — said as a word, by a respelling
/// the TTS engine reads right ("ECAM" → "ee-cam", "SID" → "sid") — and <see cref="Letters"/>
/// — said letter by letter ("APU" → "A P U"). Before this table the letter-spelled set was a
/// handful of rules in <see cref="Phraseology"/>, the shipped checklists spelled their own
/// ("A D I R S", "E CAM") and every other abbreviation reached the engine in capitals for it
/// to guess.
/// <para>
/// The pilot's copy is <c>%LOCALAPPDATA%\ProsimCompanion\config\pronunciations.json</c>
/// (seeded; engines differ, so a respelling that is right on Kokoro may need another on
/// ElevenLabs). Its entries are laid OVER <see cref="Defaults"/>: a word given an empty
/// respelling is left exactly as written. Matching is on the CAPITAL form as a whole word
/// only — "STAR" is the arrival, "star" is a star — with an optional plural "s" ("SIDs"). A
/// word entry also catches its own letter-spelled form ("A T I S", "E CAM"), so hand-spelled
/// files and model-written text say the word too.
/// </para>
/// </summary>
public sealed partial class PronunciationTable
{
    /// <summary>The user-editable file under the user config root.</summary>
    public const string FileName = "pronunciations.json";

    /// <summary>The shipped table — what <c>pronunciations.json</c> carries at install (a
    /// test holds the two together). The respellings are NOT ear-checked on every engine.</summary>
    public static PronunciationTable Defaults { get; } = new(
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ECAM"] = "ee-cam",
            ["SID"] = "sid",
            ["STAR"] = "star",
            ["ATIS"] = "ay-tis",
            ["ADIRS"] = "ay-dirs",
            ["TOGA"] = "toe-ga",
            ["TCAS"] = "tee-cas",
            ["METAR"] = "mee-tar",
            ["NOTAM"] = "no-tam",
            ["ACARS"] = "ay-cars",
            ["RNAV"] = "ar-nav",
            ["SIGMET"] = "sig-met",
            ["TAF"] = "taff",
            ["CAT"] = "cat",
            ["MAC"] = "mac",
            ["PAX"] = "pax",
            ["NAV"] = "nav",
        },
        [
            // The set Phraseology spelled before this table (Prosim2FO lineage).
            "ILS", "RVR", "QNH", "QFE", "VOR", "DME", "NDB", "ADF", "MSA", "MDA", "MCDU",
            "VLS", "VFE", "VMO", "MMO",
            // Said as letters by crews; left to the engine until 2026-11-14.
            "APU", "FMS", "FMGC", "FCU", "PFD", "ND", "ATC", "GPU", "GPS", "GLS", "LDA", "OFP", "FOB",
            "ZFW", "TOW", "LMC", "CG", "ETA", "UTC", "OAT", "IAS", "MEL", "QRH", "PA", "PTU", "RTO",
            "SRS", "RA", "TA", "VHF", "FO", "GSX", "EGPWS", "CPDLC",
        ]);

    private readonly Regex? _pattern;
    private readonly Dictionary<string, string> _spoken = new(StringComparer.Ordinal);

    /// <summary>Capital abbreviation → respelling.</summary>
    public IReadOnlyDictionary<string, string> Words { get; }

    /// <summary>Capital abbreviations said letter by letter.</summary>
    public IReadOnlyList<string> Letters { get; }

    [GeneratedRegex(@"^[A-Z][A-Z0-9]{1,7}$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidAbbreviation();

    public PronunciationTable(IReadOnlyDictionary<string, string> words, IReadOnlyList<string> letters)
    {
        ArgumentNullException.ThrowIfNull(words);
        ArgumentNullException.ThrowIfNull(letters);

        var cleanWords = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (abbreviation, respelling) in words)
        {
            var name = (abbreviation ?? "").Trim();
            if (ValidAbbreviation().IsMatch(name))
            {
                cleanWords[name] = (respelling ?? "").Trim();
            }
        }

        var cleanLetters = letters
            .Select(l => (l ?? "").Trim())
            .Where(l => ValidAbbreviation().IsMatch(l) && !cleanWords.ContainsKey(l))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Words = cleanWords;
        Letters = cleanLetters;

        var alternatives = new List<string>();
        foreach (var (abbreviation, respelling) in cleanWords)
        {
            if (respelling.Length == 0)
            {
                continue; // "leave as written"
            }

            _spoken[abbreviation] = respelling;
            alternatives.Add(abbreviation);

            // The letter-spelled form says the word too ("A T I S", and the old "E CAM").
            var spaced = string.Join(" ", abbreviation.ToCharArray());
            _spoken[spaced] = respelling;
            alternatives.Add(spaced);
            if (abbreviation == "ECAM")
            {
                _spoken["E CAM"] = respelling;
                alternatives.Add("E CAM");
            }
        }

        foreach (var abbreviation in cleanLetters)
        {
            _spoken[abbreviation] = string.Join(" ", abbreviation.ToCharArray());
            alternatives.Add(abbreviation);
        }

        if (alternatives.Count > 0)
        {
            // Longest first, so "A D I R S" wins over a shorter entry inside it.
            var body = string.Join("|", alternatives.OrderByDescending(a => a.Length).Select(Regex.Escape));
            _pattern = new Regex(
                @"(?<![A-Za-z0-9])(?<name>" + body + @")(?<plural>s)?(?![A-Za-z0-9])",
                RegexOptions.CultureInvariant | RegexOptions.Compiled);
        }
    }

    /// <summary>The text with every listed abbreviation in its spoken form. Idempotent: a
    /// respelling is never itself an entry (entries are capitals, two characters or more).</summary>
    public string Apply(string text)
    {
        if (_pattern is null || string.IsNullOrEmpty(text))
        {
            return text;
        }

        return _pattern.Replace(text, m => _spoken[m.Groups["name"].Value] + m.Groups["plural"].Value);
    }

    /// <summary>This table with another laid over it — the pilot's file over the shipped
    /// defaults. An abbreviation the other table lists moves to the list it names there.</summary>
    public PronunciationTable Over(PronunciationTable defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        var words = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (abbreviation, respelling) in defaults.Words)
        {
            if (!Letters.Contains(abbreviation, StringComparer.Ordinal))
            {
                words[abbreviation] = respelling;
            }
        }

        foreach (var (abbreviation, respelling) in Words)
        {
            words[abbreviation] = respelling;
        }

        return new PronunciationTable(words, [.. defaults.Letters.Where(l => !words.ContainsKey(l)), .. Letters]);
    }

    /// <summary>The pilot's file laid over <see cref="Defaults"/>; the defaults alone when the
    /// file is missing or unreadable (logged — never a failure).</summary>
    public static PronunciationTable LoadUserFile(Microsoft.Extensions.Logging.ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        try
        {
            var path = Configuration.UserConfigPaths.File(FileName);
            if (!File.Exists(path))
            {
                Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(logger, "{File} not found — the built-in pronunciations stand", FileName);
                return Defaults;
            }

            if (Parse(File.ReadAllText(path)) is not { } user)
            {
                Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(logger, "{File} is not readable — the built-in pronunciations stand", FileName);
                return Defaults;
            }

            var table = user.Over(Defaults);
            Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(
                logger, "Pronunciations loaded: {Words} said as words, {Letters} said as letters", table.Words.Count, table.Letters.Count);
            return table;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(logger, ex, "{File} load failed — the built-in pronunciations stand", FileName);
            return Defaults;
        }
    }

    /// <summary>Pure parse of the file text
    /// (<c>{"words": {"ECAM": "ee-cam"}, "letters": ["APU"]}</c>); null when the text is not
    /// readable JSON of that shape — the caller keeps the defaults. Invalid entries are dropped.</summary>
    public static PronunciationTable? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var words = new Dictionary<string, string>(StringComparer.Ordinal);
            if (document.RootElement.TryGetProperty("words", out var wordsElement) && wordsElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in wordsElement.EnumerateObject())
                {
                    if (entry.Value.ValueKind == JsonValueKind.String)
                    {
                        words[entry.Name] = entry.Value.GetString() ?? "";
                    }
                }
            }

            var letters = new List<string>();
            if (document.RootElement.TryGetProperty("letters", out var lettersElement) && lettersElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in lettersElement.EnumerateArray())
                {
                    if (entry.ValueKind == JsonValueKind.String)
                    {
                        letters.Add(entry.GetString() ?? "");
                    }
                }
            }

            return new PronunciationTable(words, letters);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
