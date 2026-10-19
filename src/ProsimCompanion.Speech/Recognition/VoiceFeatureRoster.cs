namespace ProsimCompanion.Speech.Recognition;

/// <summary>How a feature lays claim to one utterance — the rank the router compares before
/// any declared precedence.</summary>
public enum VoiceClaim
{
    /// <summary>The utterance IS one of the feature's phrases, normalized. Such an owner is
    /// offered the text before every other feature (issue #137).</summary>
    ExactPhrase = 0,

    /// <summary>Anything else — the feature keeps its declared place in the order.</summary>
    Declared = 1,
}

/// <summary>
/// The roster of voice features in dispatch order (candidate 5 of the 2026-10 architecture
/// review). Built once from the registered <see cref="IVoiceFeature"/> set and sorted by each
/// feature's declared <see cref="VoiceFeatureDeclaration.Precedence"/>, so the DI file no
/// longer carries the order and the router no longer special-cases it. The two rules the
/// router needs are rank comparisons here: a feature's claim on an utterance (exact phrase
/// first, issue #137), then its declared precedence; a tie keeps registration order (stable
/// sort — only test fakes ever tie).
/// </summary>
public sealed class VoiceFeatureRoster
{
    private readonly IReadOnlyList<IVoiceFeature> _ordered;

    public VoiceFeatureRoster(IEnumerable<IVoiceFeature> features)
    {
        ArgumentNullException.ThrowIfNull(features);
        _ordered =
        [
            .. features
                .Select((feature, registered) => (Feature: feature, Registered: registered))
                .OrderBy(entry => entry.Feature.Declaration.Precedence)
                .ThenBy(entry => entry.Registered)
                .Select(entry => entry.Feature),
        ];
    }

    /// <summary>Every feature, switched on or not, in dispatch order — the drawer lists them
    /// all so the pilot learns what they could turn on.</summary>
    public IReadOnlyList<IVoiceFeature> All => _ordered;

    /// <summary>The switched-on features in dispatch order — the grammar contributors. A
    /// disabled feature contributes no phrases and is never offered an utterance (the router
    /// owns the gate, campaign #82).</summary>
    public IEnumerable<IVoiceFeature> Enabled() => _ordered.Where(f => f.Enabled);

    /// <summary>The switched-on value parsers (FCU, radios, read-backs, the arrival-runway
    /// changer) in dispatch order — they get the RAW transcription before snapping so numbers
    /// survive.</summary>
    public IEnumerable<IVoiceFeature> EnabledValueParsers() => _ordered.Where(f => f.Enabled && f.ValueParse);

    /// <summary>True when the text, normalized, equals a phrase of a switched-on feature that
    /// is NOT a value parser (issue #137): the parsers key on words, so "tune the ils" died as
    /// "Say again the frequency" and "say v speeds" / "altitude star" / "one hundred knots" as
    /// "couldn't read the speed" before their features ever saw them. Such text skips the raw
    /// value-parse pass; the parsers stay the path for free-form instructions ("set heading
    /// one two zero"). A disabled owner owns nothing — its phrase is free-form text again.</summary>
    public bool OwnsExactPhrase(string text)
    {
        var normalized = CommandMatcher.Normalize(text);
        return normalized.Length > 0
            && _ordered.Any(f => f.Enabled && !f.ValueParse && HasPhrase(f, normalized));
    }

    /// <summary>True when the (snapped) phrase belongs to a switched-on acknowledge-only
    /// feature — the router may fire it on a gray-band match without the clarifier in a sterile
    /// moment (issue #177).</summary>
    public bool OwnsAcknowledgeOnlyPhrase(string text)
    {
        var normalized = CommandMatcher.Normalize(text);
        return normalized.Length > 0
            && _ordered.Any(f => f.Enabled && f.AcknowledgeOnly && HasPhrase(f, normalized));
    }

    /// <summary>The switched-on features in the order they are offered the (snapped) text: by
    /// <see cref="ClaimOf"/> first — a feature that contributes the text as one of ITS phrases
    /// goes before everyone (issue #137: registration order put the radio/FCU parsers ahead of
    /// all, and their keyword matching claimed "tune the ils" and "say v speeds" on this pass
    /// too) — then by declared precedence.</summary>
    public IReadOnlyList<IVoiceFeature> Candidates(string text)
    {
        var normalized = CommandMatcher.Normalize(text);
        return [.. _ordered.Where(f => f.Enabled).OrderBy(f => ClaimOf(f, normalized))];
    }

    /// <summary>The rank a feature's claim on <paramref name="normalized"/> has, before its
    /// declared precedence is compared.</summary>
    public static VoiceClaim ClaimOf(IVoiceFeature feature, string normalized)
    {
        ArgumentNullException.ThrowIfNull(feature);
        return HasPhrase(feature, normalized) ? VoiceClaim.ExactPhrase : VoiceClaim.Declared;
    }

    private static bool HasPhrase(IVoiceFeature feature, string normalized)
        => feature.Phrases.Any(p => string.Equals(CommandMatcher.Normalize(p), normalized, StringComparison.Ordinal));
}
