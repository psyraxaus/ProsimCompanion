namespace ProsimCompanion.Core.Speech;

/// <summary>
/// A configured voice as <c>provider:id</c> — <c>google:en-AU-Chirp3-HD-Aoede</c>,
/// <c>elevenlabs:EXAVITQu4vr4xnSDxMaL</c>, <c>kokoro:bm_george</c>, <c>winrt:Microsoft Catherine</c>
/// — so one dropdown can offer every provider's voices and the chain knows which provider
/// must speak that role (owner, 2026-10-12). A value without a known provider prefix is a
/// legacy plain id: it is handed to whichever provider speaks, exactly as before.
/// </summary>
public readonly record struct VoiceRef(string? Provider, string Id)
{
    /// <summary>Chain order; also the only prefixes <see cref="Parse"/> recognises, so a
    /// Google name or an ElevenLabs id can never be mistaken for a prefix.</summary>
    public static readonly IReadOnlyList<string> Providers = ["kokoro", "elevenlabs", "google", "winrt", "sapi"];

    /// <summary>Human names for the picker's group labels and the Status page.</summary>
    public static string DisplayName(string provider) => provider switch
    {
        "kokoro" => "Kokoro",
        "elevenlabs" => "ElevenLabs",
        "google" => "Google",
        "winrt" => "Windows",
        "sapi" => "Windows (SAPI)",
        _ => provider,
    };

    public bool IsEmpty => Id.Length == 0;

    /// <summary>True for a provider-qualified voice — the chain promotes that provider.</summary>
    public bool HasProvider => Provider is not null;

    /// <summary>Parses a configured value. Whitespace is trimmed; an empty value is
    /// <c>(null, "")</c>; <c>kokoro:bm_george</c> is <c>("kokoro", "bm_george")</c>;
    /// <c>bm_george</c> is <c>(null, "bm_george")</c>.</summary>
    public static VoiceRef Parse(string? value)
    {
        var text = (value ?? "").Trim();
        var colon = text.IndexOf(':');
        if (colon > 0)
        {
            var prefix = text[..colon].ToLowerInvariant();
            if (Providers.Contains(prefix))
            {
                return new VoiceRef(prefix, text[(colon + 1)..].Trim());
            }
        }

        return new VoiceRef(null, text);
    }

    /// <summary>The stored form, <c>provider:id</c>.</summary>
    public static string Format(string provider, string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentNullException.ThrowIfNull(id);
        return $"{provider.ToLowerInvariant()}:{id.Trim()}";
    }

    /// <summary>The voice id <paramref name="providerName"/> should use for a configured
    /// value: the id itself when the value names that provider or no provider at all
    /// (legacy); null when the value names a different provider — that provider's own
    /// fallback voice applies and the caller may skip warming it.</summary>
    public static bool TryVoiceFor(string? configured, string providerName, out string? voice)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        var parsed = Parse(configured);
        if (parsed.IsEmpty)
        {
            voice = null;
            return true;
        }

        if (!parsed.HasProvider)
        {
            voice = parsed.Id;
            return true;
        }

        if (string.Equals(parsed.Provider, providerName, StringComparison.OrdinalIgnoreCase))
        {
            voice = parsed.Id;
            return true;
        }

        voice = null;
        return false;
    }

    public override string ToString() => Provider is null ? Id : $"{Provider}:{Id}";
}
