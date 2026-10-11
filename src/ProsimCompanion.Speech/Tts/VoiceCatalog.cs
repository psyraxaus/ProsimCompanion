using System.Globalization;
using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Speech;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Speech.Tts;

/// <summary>
/// The Voices page's one-stop list: every provider's voices as <see cref="VoiceEntry"/> rows
/// (owner, 2026-10-12). Kokoro and ElevenLabs go through their catalogs (draft URL / key);
/// Google and the two Windows providers list themselves (<see cref="IVoiceListingTtsProvider"/>).
/// The pure row builders are public so the region / gender mapping is unit-tested without
/// any service.
/// </summary>
public sealed class VoiceCatalog : IVoiceCatalog
{
    private readonly IKokoroVoiceCatalog _kokoro;
    private readonly IElevenLabsVoiceCatalog _elevenLabs;
    private readonly IReadOnlyDictionary<string, IVoiceListingTtsProvider> _listing;
    private readonly ILogger<VoiceCatalog> _logger;

    public VoiceCatalog(
        IKokoroVoiceCatalog kokoro,
        IElevenLabsVoiceCatalog elevenLabs,
        IEnumerable<ITtsProvider> providers,
        ILogger<VoiceCatalog> logger)
    {
        ArgumentNullException.ThrowIfNull(kokoro);
        ArgumentNullException.ThrowIfNull(elevenLabs);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(logger);
        _kokoro = kokoro;
        _elevenLabs = elevenLabs;
        _listing = providers
            .OfType<IVoiceListingTtsProvider>()
            .ToDictionary(p => ((ITtsProvider)p).Name, p => p, StringComparer.OrdinalIgnoreCase);
        _logger = logger;
    }

    public async Task<IReadOnlyList<VoiceEntry>> ListVoicesAsync(string provider, SpeechOptions draft, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentNullException.ThrowIfNull(draft);

        switch (provider.ToLowerInvariant())
        {
            case "kokoro":
            {
                if (string.IsNullOrWhiteSpace(draft.KokoroBaseUrl))
                {
                    return [];
                }

                var ids = await _kokoro.ListVoiceIdsAsync(draft.KokoroBaseUrl, cancellationToken).ConfigureAwait(false);
                return ids.Select(FromKokoro).ToArray();
            }

            case "elevenlabs":
            {
                if (string.IsNullOrWhiteSpace(draft.ElevenLabsApiKey))
                {
                    return [];
                }

                var voices = await _elevenLabs.FetchPremadeVoicesAsync(draft.ElevenLabsBaseUrl, draft.ElevenLabsApiKey, cancellationToken).ConfigureAwait(false);
                return voices.Select(v => FromElevenLabs(v.Id, v.Name, v.Accent, v.Gender)).ToArray();
            }

            default:
            {
                if (!_listing.TryGetValue(provider, out var lister))
                {
                    _logger.LogDebug("No voice listing for provider {Provider}", provider);
                    return [];
                }

                return await lister.ListVoicesAsync(draft, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>A Kokoro id with the published sheet's grades for the details line.</summary>
    public static VoiceEntry FromKokoro(string id)
    {
        var voice = KokoroVoices.Describe(id);
        return new VoiceEntry("kokoro", voice.Id, voice.Name, voice.Region, voice.Gender, voice.Grade, voice.Blurb);
    }

    /// <summary>An ElevenLabs premade voice; the accent label ("american", "british") is the
    /// region, capitalised, "Other" when the label is missing.</summary>
    public static VoiceEntry FromElevenLabs(string id, string name, string? accent, string? gender)
    {
        var region = string.IsNullOrWhiteSpace(accent) ? "Other" : Capitalise(accent.Trim());
        var sex = NormaliseGender(gender);
        var blurb = $"ElevenLabs premade voice. {region}{(sex.Length > 0 ? ", " + sex : "")}. Cloud, billed per character against your monthly budget.";
        return new VoiceEntry("elevenlabs", id, name, region, sex, null, blurb);
    }

    /// <summary>A Google voice; the region is the language's display name ("English
    /// (Australia)"), the name the persona after the model family ("Aoede").</summary>
    public static VoiceEntry FromGoogle(string voiceName, string? languageCode, string? gender)
    {
        var region = RegionFromCulture(languageCode);
        var sex = NormaliseGender(gender);
        var dash = voiceName.LastIndexOf('-');
        var persona = dash >= 0 && dash < voiceName.Length - 1 ? voiceName[(dash + 1)..] : voiceName;
        var family = voiceName.Contains("Chirp3-HD", StringComparison.OrdinalIgnoreCase) ? "Chirp 3 HD" : "Google";
        var blurb = $"Google {family} voice {voiceName}. {region}{(sex.Length > 0 ? ", " + sex : "")}. Cloud, billed per character against your monthly budget.";
        return new VoiceEntry("google", voiceName, persona, region, sex, null, blurb);
    }

    /// <summary>A Windows voice (WinRT or SAPI) by display name; the region is the language's
    /// display name.</summary>
    public static VoiceEntry FromWindows(string provider, string displayName, string? languageTag, string? gender)
    {
        var region = RegionFromCulture(languageTag);
        var sex = NormaliseGender(gender);
        var name = displayName.StartsWith("Microsoft ", StringComparison.OrdinalIgnoreCase) ? displayName[10..] : displayName;
        var kind = provider == "winrt" ? "Windows voice" : "Windows SAPI voice";
        var blurb = $"{kind} \"{displayName}\". {region}{(sex.Length > 0 ? ", " + sex : "")}. Offline, on this PC.";
        return new VoiceEntry(provider, displayName, name, region, sex, null, blurb);
    }

    private static string RegionFromCulture(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return "Other";
        }

        try
        {
            return CultureInfo.GetCultureInfo(tag.Trim()).EnglishName;
        }
        catch (CultureNotFoundException)
        {
            return tag.Trim();
        }
    }

    private static string NormaliseGender(string? gender)
    {
        var g = (gender ?? "").Trim().ToLowerInvariant();
        return g switch
        {
            "female" or "f" => "female",
            "male" or "m" => "male",
            _ => "",
        };
    }

    private static string Capitalise(string s)
        => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
