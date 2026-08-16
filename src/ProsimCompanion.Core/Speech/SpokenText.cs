using ProsimCompanion.Core.Airports;

namespace ProsimCompanion.Core.Speech;

/// <summary>
/// Spoken text (CONTEXT.md): the TTS-ready rendering of an aviation identifier, including the
/// fallback when the friendly form is unknown. One module (campaign #81) — the 2026-08 review
/// found four runway renderers with four blank-input policies, three ICAO spellers (two
/// letter-spelled "E G C C"), and four different unknown-airport fallbacks smeared across the
/// speech pillar. Pronunciation is decided here once; ABSENCE wording ("unavailable" vs "the
/// arrival runway") stays with the caller — that is context, not pronunciation.
/// </summary>
public interface ISpokenText
{
    /// <summary>"16R" → "one six right" (strips an RW prefix); null when there is no runway
    /// to speak — the caller phrases absence.</summary>
    string? Runway(string? designator);

    /// <summary>Friendly name when known ("Heathrow"), NATO-spelled ICAO otherwise
    /// ("Echo Golf Charlie Charlie" — never letter-spelled "E G C C", which the TTS engines
    /// mangle; issue #68). Empty for blank input.</summary>
    string Airport(string? icao);

    /// <summary>NATO spelling of a code, letter by letter, digits as ICAO digit words.</summary>
    string Icao(string? code);

    /// <summary>Procedure/airway identifier ("VOLA3V" → "VOLA three Victor") — see
    /// <see cref="NatoPhonetics.SpeakIdentifier"/>.</summary>
    string Identifier(string? identifier);

    /// <summary>"110.30" → "one one zero decimal three zero" (ICAO digits, 9 = "niner").</summary>
    string Frequency(string? frequency);
}

/// <summary>Default <see cref="ISpokenText"/>: <see cref="NatoPhonetics"/> for rendering, an
/// optional <see cref="IAirportNames"/> (the DFD-backed resolver, absent without nav data)
/// for friendly airport names.</summary>
public sealed class SpokenText : ISpokenText
{
    private readonly IAirportNames? _airportNames;

    public SpokenText(IAirportNames? airportNames = null) => _airportNames = airportNames;

    /// <inheritdoc />
    public string? Runway(string? designator) => RunwayOrNull(designator);

    /// <summary>Static core of <see cref="Runway"/> for the pillar's static formatting
    /// helpers; identical semantics.</summary>
    public static string? RunwayOrNull(string? designator)
    {
        if (string.IsNullOrWhiteSpace(designator))
        {
            return null;
        }

        var s = designator.Trim().ToUpperInvariant();
        if (s.StartsWith("RW", StringComparison.Ordinal))
        {
            s = s[2..];
        }

        var digits = new string([.. s.TakeWhile(char.IsAsciiDigit)]);
        if (digits.Length == 0)
        {
            return null;
        }

        var side = s.SkipWhile(char.IsAsciiDigit).FirstOrDefault() switch
        {
            'L' => " left",
            'R' => " right",
            'C' => " center",
            _ => "",
        };
        return string.Join(" ", digits.Select(d => NatoPhonetics.DigitWords[d - '0'])) + side;
    }

    /// <inheritdoc />
    public string Airport(string? icao)
        => string.IsNullOrWhiteSpace(icao)
            ? ""
            : _airportNames?.SpokenName(icao) ?? Icao(icao);

    /// <inheritdoc />
    public string Icao(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return "";
        }

        var parts = new List<string>();
        foreach (var c in code.Trim())
        {
            if (c is >= '0' and <= '9')
            {
                parts.Add(NatoPhonetics.DigitWords[c - '0']);
            }
            else if (NatoPhonetics.Word(c) is { } word)
            {
                parts.Add(word);
            }
        }

        return string.Join(" ", parts);
    }

    /// <inheritdoc />
    public string Identifier(string? identifier) => NatoPhonetics.SpeakIdentifier(identifier);

    /// <inheritdoc />
    public string Frequency(string? frequency)
    {
        if (string.IsNullOrWhiteSpace(frequency))
        {
            return "";
        }

        var parts = new List<string>();
        foreach (var c in frequency.Trim())
        {
            if (c is >= '0' and <= '9')
            {
                parts.Add(NatoPhonetics.DigitWords[c - '0']);
            }
            else if (c is '.' or ',')
            {
                parts.Add("decimal");
            }
        }

        return string.Join(" ", parts);
    }
}
