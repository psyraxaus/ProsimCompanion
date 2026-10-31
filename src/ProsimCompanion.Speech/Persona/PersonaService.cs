using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Persona;

namespace ProsimCompanion.Speech.Persona;

/// <summary>Which speech surface a style decision is for — each has its own persona toggle.</summary>
public enum PersonaStyleCategory
{
    Briefing,
    Debrief,
    Advisory,
}

/// <summary>Acknowledgement kinds the persona can vary.</summary>
public enum AckKind
{
    AreYouSure,
    DidNotCatch,
}

/// <summary>
/// The persona in force right now (issue #194): the roster FO's axes while the roster is
/// active and someone is on duty, else the <see cref="PersonaOptions"/> values. One record so
/// the fragment, the page and the gate intro read the same person.
/// </summary>
/// <param name="FromRoster">True when the values came from the drawn FO.</param>
public sealed record EffectivePersona(
    string Name,
    string Experience,
    string Formality,
    int Chattiness,
    string Confidence,
    bool FromRoster);

/// <summary>
/// The FO persona (Prosim2FO semantics with its warts fixed): builds the personality fragment
/// prepended to every LLM style/composition prompt, and varies acknowledgements. The fragment
/// carries the hard rule that only wording and tone may change — never an operational fact.
/// Acknowledgement variation draws RANDOM picks from the same phrases.json-backed pools the
/// round-robin uses (the predecessor's hardcoded pools silently shadowed the user's file).
/// "Quiet please" (QuietState) zeroes the effective chattiness for the session.
/// <para>Since issue #194 the four person axes (name, experience, formality, chattiness) and
/// the confidence axis come from the drawn roster FO while the roster is active; the style
/// switches, acknowledgement variation and the restyle budget stay the options' own.</para>
/// </summary>
public sealed class PersonaService
{
    /// <summary>The one sentence every fragment must carry, whatever the axes say.</summary>
    public const string FactLockSentence =
        "Let only your wording and tone reflect this personality — NEVER add, remove, or change any " +
        "operational fact, number, identifier, or callout. The professional content must stay identical. ";

    private readonly IOptionsMonitor<PersonaOptions> _options;
    private readonly PhraseBank _phrases;
    private readonly QuietState _quiet;
    private readonly IFoRosterService? _roster;
    private readonly Random _random = new();
    private readonly object _randomGate = new();

    public PersonaService(
        IOptionsMonitor<PersonaOptions> options,
        PhraseBank phrases,
        QuietState quiet,
        IFoRosterService? roster = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(phrases);
        ArgumentNullException.ThrowIfNull(quiet);

        _options = options;
        _phrases = phrases;
        _quiet = quiet;
        _roster = roster;
    }

    public bool Enabled => _options.CurrentValue.Enabled;

    /// <summary>The persona in force: the roster FO's axes when the roster is active and
    /// someone is on duty, else the options'. Chattiness here is the configured figure — see
    /// <see cref="Chattiness"/> for the quiet-latched one.</summary>
    public EffectivePersona Effective
    {
        get
        {
            var options = _options.CurrentValue;
            if (_roster is { IsActive: true } roster && roster.Current is { } fo)
            {
                return new EffectivePersona(fo.Name, fo.Experience, fo.Formality, fo.Chattiness, fo.Confidence, FromRoster: true);
            }

            return new EffectivePersona(
                options.Name ?? "",
                options.Experience ?? "standard",
                options.Formality ?? "standard",
                options.Chattiness,
                options.Confidence ?? "steady",
                FromRoster: false);
        }
    }

    /// <summary>Effective chattiness: the persona's 0–3, forced to 0 once the crew asked for
    /// quiet (session latch).</summary>
    public int Chattiness => _quiet.IsQuiet ? 0 : Math.Clamp(Effective.Chattiness, 0, 3);

    /// <summary>True when this category should be LLM-styled at all (persona on + its toggle).</summary>
    public bool StylingEnabled(PersonaStyleCategory category)
    {
        var options = _options.CurrentValue;
        return options.Enabled && category switch
        {
            PersonaStyleCategory.Briefing => options.StyleBriefings,
            PersonaStyleCategory.Debrief => options.StyleDebrief,
            PersonaStyleCategory.Advisory => options.StyleAdvisories,
            _ => false,
        };
    }

    /// <summary>The personality fragment for an LLM system prompt — empty when the persona is
    /// off or the category's styling toggle is off, so composers can prepend unconditionally.
    /// Wording ported from Prosim2FO's PersonaService.SystemPromptFragment; the confidence
    /// clause (issue #194) sits between the manner and the warmth.</summary>
    public string SystemPromptFragment(PersonaStyleCategory category)
    {
        if (!StylingEnabled(category))
        {
            return "";
        }

        return Fragment(Effective, Chattiness);
    }

    /// <summary>The fragment for a given persona — pure, so the test can cover every
    /// experience × formality × confidence combination without options plumbing.</summary>
    public static string Fragment(EffectivePersona persona, int effectiveChattiness)
    {
        ArgumentNullException.ThrowIfNull(persona);
        var experience = (persona.Experience ?? "").ToLowerInvariant() switch
        {
            "junior" => "a keen, relatively junior First Officer",
            "senior" => "a seasoned, senior First Officer",
            _ => "an experienced First Officer",
        };
        var manner = (persona.Formality ?? "").ToLowerInvariant() switch
        {
            "casual" => "relaxed and personable",
            "formal" => "crisp and formal",
            _ => "professional and approachable",
        };
        var name = string.IsNullOrWhiteSpace(persona.Name) ? "" : $"Your name is {persona.Name.Trim()}. ";
        var confidence = ConfidenceClause(persona.Confidence);
        var warmth = effectiveChattiness >= 2
            ? "A little natural warmth is welcome. "
            : "Keep it economical. ";

        return $"{name}You are {experience}; your manner is {manner}. {confidence}{warmth}" + FactLockSentence;
    }

    /// <summary>The confidence axis as wording-and-tone guidance: nervous double-checks and
    /// asks, assured states; steady adds nothing, so a persona from before #194 reads the same.</summary>
    public static string ConfidenceClause(string? confidence)
        => (confidence ?? "").Trim().ToLowerInvariant() switch
        {
            "nervous" => "You double-check yourself, you read back carefully, and you are quick to ask the captain rather than assume. ",
            "assured" => "You are concise and unhesitating; you state things, you don't ask. ",
            _ => "",
        };

    /// <summary>Acknowledgement text: the deterministic round-robin fallback when persona (or
    /// acknowledgement variation) is off, else a random pick from the same phrases.json pool.</summary>
    public string Acknowledge(AckKind kind, string deterministicFallback)
    {
        ArgumentNullException.ThrowIfNull(deterministicFallback);
        var options = _options.CurrentValue;
        if (!options.Enabled || !options.VaryAcknowledgements)
        {
            return deterministicFallback;
        }

        var pool = kind switch
        {
            AckKind.AreYouSure => _phrases.AreYouSure,
            AckKind.DidNotCatch => _phrases.DidNotCatch,
            _ => [],
        };
        if (pool.Count == 0)
        {
            return deterministicFallback;
        }

        lock (_randomGate)
        {
            return pool[_random.Next(pool.Count)];
        }
    }
}
