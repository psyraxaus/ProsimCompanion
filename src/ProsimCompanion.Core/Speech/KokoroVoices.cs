namespace ProsimCompanion.Core.Speech;

/// <summary>
/// The Kokoro voice sheet, grouped by region, for the settings-page voice pickers. Kokoro
/// voice ids encode language and gender in their prefix (<c>bm_george</c> = British,
/// male), so ANY id the server reports — the v1.0 sheet voices, the legacy <c>_v0</c>
/// voices kokoro-fastapi ships, or a user's own blended voice — can be filed under a region
/// without a lookup. The grades come from Kokoro's own VOICES.md (hexgrad/Kokoro-82M,
/// fetched 2026-10-12): the author's estimate of the quality AND quantity of each voice's
/// training data, not a listening test. Spanish and Portuguese voices carry no grades there.
/// </summary>
public static class KokoroVoices
{
    /// <summary>One voice as the picker shows it. <see cref="Grade"/>, <see cref="TargetQuality"/>
    /// and <see cref="Training"/> are null for a voice the sheet does not grade.</summary>
    public sealed record Voice(
        string Id,
        string Name,
        string Region,
        string Gender,
        string? Grade,
        string? TargetQuality,
        string? Training,
        string? Note)
    {
        /// <summary>True when the voice's language is not English — it reads English text with
        /// its own letter-to-sound rules, which is a novelty, not a First Officer.</summary>
        public bool NonEnglish => Region is not (AmericanEnglish or BritishEnglish or Other);

        /// <summary>Dropdown label: "George (male), grade C".</summary>
        public string Label => Grade is null ? $"{Name} ({Gender})" : $"{Name} ({Gender}), grade {Grade}";

        /// <summary>Two or three plain sentences for the details line under the picker.</summary>
        public string Blurb
        {
            get
            {
                var parts = new List<string> { Gender.Length > 0 ? $"{Region}, {Gender}." : $"{Region}." };
                if (Grade is not null)
                {
                    var detail = new List<string>();
                    if (TargetQuality is not null) detail.Add($"reference audio quality {TargetQuality}");
                    if (Training is not null) detail.Add($"trained on {Training} of audio");
                    parts.Add(detail.Count > 0
                        ? $"Overall grade {Grade} ({string.Join(", ", detail)})."
                        : $"Overall grade {Grade}.");
                }
                else if (Region != Other)
                {
                    parts.Add("Kokoro publishes no grade for this voice.");
                }
                if (Note is not null) parts.Add(Note);
                if (NonEnglish) parts.Add("Reads English with its own language's sounds. Try it for fun, not for the First Officer.");
                return string.Join(" ", parts);
            }
        }
    }

    public const string AmericanEnglish = "American English";
    public const string BritishEnglish = "British English";
    public const string Other = "Other";

    /// <summary>Region display order: the two English groups first (the only ones that make a
    /// usable FO), then the rest as the sheet lists them, then anything unrecognised.</summary>
    private static readonly string[] RegionOrder =
    [
        AmericanEnglish, BritishEnglish, "Japanese", "Mandarin Chinese", "Spanish", "French",
        "Hindi", "Italian", "Brazilian Portuguese", Other,
    ];

    private static readonly Dictionary<char, string> RegionByPrefix = new()
    {
        ['a'] = AmericanEnglish, ['b'] = BritishEnglish, ['j'] = "Japanese", ['z'] = "Mandarin Chinese",
        ['e'] = "Spanish", ['f'] = "French", ['h'] = "Hindi", ['i'] = "Italian", ['p'] = "Brazilian Portuguese",
    };

    private const string HH = "10 to 100 hours";
    private const string H = "1 to 10 hours";
    private const string MM = "10 to 100 minutes";
    private const string M = "under 10 minutes";

    // (id, overall grade, target quality, training duration, note) straight from VOICES.md.
    private static readonly (string Id, string? Grade, string? Quality, string? Training, string? Note)[] Sheet =
    [
        ("af_heart", "A", null, null, "Kokoro's showcase voice."),
        ("af_alloy", "C", "B", MM, null),
        ("af_aoede", "C+", "B", H, null),
        ("af_bella", "A-", "A", HH, null),
        ("af_jessica", "D", "C", MM, null),
        ("af_kore", "C+", "B", H, null),
        ("af_nicole", "B-", "B", HH, "Soft, close-mic delivery."),
        ("af_nova", "C", "B", MM, null),
        ("af_river", "D", "C", MM, null),
        ("af_sarah", "C+", "B", H, null),
        ("af_sky", "C-", "B", M, null),
        ("am_adam", "F+", "D", H, null),
        ("am_echo", "D", "C", MM, null),
        ("am_eric", "D", "C", MM, null),
        ("am_fenrir", "C+", "B", H, null),
        ("am_liam", "D", "C", MM, null),
        ("am_michael", "C+", "B", H, null),
        ("am_onyx", "D", "C", MM, null),
        ("am_puck", "C+", "B", H, null),
        ("am_santa", "D-", "C", M, null),
        ("bf_alice", "D", "C", MM, null),
        ("bf_emma", "B-", "B", HH, null),
        ("bf_isabella", "C", "B", MM, null),
        ("bf_lily", "D", "C", MM, null),
        ("bm_daniel", "D", "C", MM, null),
        ("bm_fable", "C", "B", MM, null),
        ("bm_george", "C", "B", MM, null),
        ("bm_lewis", "D+", "C", H, null),
        ("jf_alpha", "C+", "B", H, null),
        ("jf_gongitsune", "C", "B", MM, null),
        ("jf_nezumi", "C-", "B", M, null),
        ("jf_tebukuro", "C", "B", MM, null),
        ("jm_kumo", "C-", "B", M, null),
        ("zf_xiaobei", "D", "C", MM, null),
        ("zf_xiaoni", "D", "C", MM, null),
        ("zf_xiaoxiao", "D", "C", MM, null),
        ("zf_xiaoyi", "D", "C", MM, null),
        ("zm_yunjian", "D", "C", MM, null),
        ("zm_yunxi", "D", "C", MM, null),
        ("zm_yunxia", "D", "C", MM, null),
        ("zm_yunyang", "D", "C", MM, null),
        ("ef_dora", null, null, null, null),
        ("em_alex", null, null, null, null),
        ("em_santa", null, null, null, null),
        ("ff_siwis", "B-", "B", "under 11 hours", null),
        ("hf_alpha", "C", "B", MM, null),
        ("hf_beta", "C", "B", MM, null),
        ("hm_omega", "C", "B", MM, null),
        ("hm_psi", "C", "B", MM, null),
        ("if_sara", "C", "B", MM, null),
        ("im_nicola", "C", "B", MM, null),
        ("pf_dora", null, null, null, null),
        ("pm_alex", null, null, null, null),
        ("pm_santa", null, null, null, null),
    ];

    private static readonly Dictionary<string, Voice> KnownById =
        Sheet.ToDictionary(s => s.Id, s => Build(s.Id, s.Grade, s.Quality, s.Training, s.Note), StringComparer.OrdinalIgnoreCase);

    /// <summary>The v1.0 sheet voices, in sheet order — the picker's list when no server has
    /// answered yet (or Kokoro is off).</summary>
    public static IReadOnlyList<Voice> Known { get; } = Sheet.Select(s => KnownById[s.Id]).ToArray();

    /// <summary>Describes any id: a sheet voice with its grades, or a prefix-only reading for an
    /// id the server reported that the sheet does not list (legacy <c>_v0</c> voices, blends).
    /// An id without a recognisable prefix lands in <see cref="Other"/> with no gender.</summary>
    public static Voice Describe(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        id = id.Trim();
        return KnownById.TryGetValue(id, out var known) ? known : Build(id, null, null, null, null);
    }

    /// <summary>True for an id the sheet lists.</summary>
    public static bool IsKnown(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return KnownById.ContainsKey(id.Trim());
    }

    /// <summary>Groups ids by region in display order; within a region, female voices first
    /// (as the sheet lists them), then by name. Duplicates and blanks are dropped.</summary>
    public static IReadOnlyList<(string Region, IReadOnlyList<Voice> Voices)> Group(IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var voices = ids
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(Describe)
            .GroupBy(v => v.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        var result = new List<(string, IReadOnlyList<Voice>)>();
        foreach (var region in RegionOrder)
        {
            var inRegion = voices
                .Where(v => v.Region == region)
                .OrderBy(v => v.Gender == "female" ? 0 : v.Gender == "male" ? 1 : 2)
                .ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (inRegion.Length > 0) result.Add((region, inRegion));
        }
        return result;
    }

    private static Voice Build(string id, string? grade, string? quality, string? training, string? note)
    {
        var underscore = id.IndexOf('_');
        var prefix = underscore == 2 ? id[..2].ToLowerInvariant() : "";
        var region = prefix.Length == 2 && RegionByPrefix.TryGetValue(prefix[0], out var r) ? r : Other;
        var gender = region == Other ? "" : prefix[1] switch { 'f' => "female", 'm' => "male", _ => "" };
        var rawName = region == Other ? id : id[(underscore + 1)..];
        var legacy = rawName.StartsWith("v0", StringComparison.OrdinalIgnoreCase) && rawName.Length > 2;
        var name = legacy ? Capitalise(rawName[2..]) + " (v0)" : Capitalise(rawName);
        if (legacy && region != Other)
        {
            note ??= "Older v0 model voice shipped by kokoro-fastapi. Not on Kokoro's voice sheet.";
        }
        return new Voice(id, name, region, gender, grade, quality, training, note);
    }

    private static string Capitalise(string s)
        => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
