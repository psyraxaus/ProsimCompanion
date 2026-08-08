using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;

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
/// The FO persona (Prosim2FO semantics with its warts fixed): builds the personality fragment
/// prepended to every LLM style/composition prompt, and varies acknowledgements. The fragment
/// carries the hard rule that only wording and tone may change — never an operational fact.
/// Acknowledgement variation draws RANDOM picks from the same phrases.json-backed pools the
/// round-robin uses (the predecessor's hardcoded pools silently shadowed the user's file).
/// "Quiet please" (QuietState) zeroes the effective chattiness for the session.
/// </summary>
public sealed class PersonaService
{
    private readonly IOptionsMonitor<PersonaOptions> _options;
    private readonly PhraseBank _phrases;
    private readonly QuietState _quiet;
    private readonly Random _random = new();
    private readonly object _randomGate = new();

    public PersonaService(
        IOptionsMonitor<PersonaOptions> options,
        PhraseBank phrases,
        QuietState quiet)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(phrases);
        ArgumentNullException.ThrowIfNull(quiet);

        _options = options;
        _phrases = phrases;
        _quiet = quiet;
    }

    public bool Enabled => _options.CurrentValue.Enabled;

    /// <summary>Effective chattiness: configured 0–3, forced to 0 once the crew asked for
    /// quiet (session latch).</summary>
    public int Chattiness => _quiet.IsQuiet ? 0 : Math.Clamp(_options.CurrentValue.Chattiness, 0, 3);

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
    /// Wording ported from Prosim2FO's PersonaService.SystemPromptFragment.</summary>
    public string SystemPromptFragment(PersonaStyleCategory category)
    {
        var options = _options.CurrentValue;
        if (!StylingEnabled(category))
        {
            return "";
        }

        var experience = options.Experience.ToLowerInvariant() switch
        {
            "junior" => "a keen, relatively junior First Officer",
            "senior" => "a seasoned, senior First Officer",
            _ => "an experienced First Officer",
        };
        var manner = options.Formality.ToLowerInvariant() switch
        {
            "casual" => "relaxed and personable",
            "formal" => "crisp and formal",
            _ => "professional and approachable",
        };
        var name = string.IsNullOrWhiteSpace(options.Name) ? "" : $"Your name is {options.Name.Trim()}. ";
        var warmth = Chattiness >= 2
            ? "A little natural warmth is welcome. "
            : "Keep it economical. ";

        return
            $"{name}You are {experience}; your manner is {manner}. {warmth}" +
            "Let only your wording and tone reflect this personality — NEVER add, remove, or change any " +
            "operational fact, number, identifier, or callout. The professional content must stay identical. ";
    }

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
