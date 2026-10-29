using System.Globalization;
using System.Text.RegularExpressions;

namespace ProsimCompanion.Core.Speech;

/// <summary>
/// The spoken form of a SID / STAR identifier the way a crew says it (owner request 2026-10-29):
/// "BPK7F" → "Brookmans Park seven Foxtrot", "LAM2X" → "Lambourne two X-ray", "RATEV3A" → "Ratev
/// three Alpha", "FISHA1" → "Fisha one". The base fix is looked up through the caller's name
/// source (the SimBrief navlog, then the DFD); an unnamed base of four letters or more is a
/// pronounceable word and is title-cased for the TTS (all-caps "FISHA" is spelled by some
/// voices), a shorter unnamed base is NATO-spelled ("Bravo Papa Kilo" — never bare letters,
/// which the voices swallow). An identifier that is not procedure-shaped falls back to
/// <see cref="NatoPhonetics.SpeakIdentifier"/>. Pure.
/// </summary>
public static partial class ProcedureSpeech
{
    [GeneratedRegex(@"^(?<base>[A-Z]{2,5})(?<number>\d)(?<designator>[A-Z]{0,2})$", RegexOptions.CultureInvariant)]
    private static partial Regex Procedure();

    /// <summary>Spoken form; <paramref name="nameOf"/> returns the base fix's stored name or null.</summary>
    public static string Speak(string? identifier, Func<string, string?> nameOf)
    {
        ArgumentNullException.ThrowIfNull(nameOf);
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return "";
        }

        var ident = identifier.Trim().ToUpperInvariant();
        var match = Procedure().Match(ident);
        if (!match.Success)
        {
            return NatoPhonetics.SpeakIdentifier(identifier);
        }

        var baseIdent = match.Groups["base"].Value;
        var parts = new List<string> { BaseWords(baseIdent, nameOf(baseIdent)) };
        parts.Add(NatoPhonetics.DigitWords[match.Groups["number"].Value[0] - '0']);
        foreach (var c in match.Groups["designator"].Value)
        {
            parts.Add(NatoPhonetics.Word(c) ?? c.ToString());
        }

        return string.Join(" ", parts);
    }

    /// <summary>The spoken base: the stored name title-cased, else the ident as a word (4+
    /// letters) or NATO-spelled (shorter).</summary>
    private static string BaseWords(string baseIdent, string? storedName)
    {
        var name = storedName?.Trim();
        if (!string.IsNullOrEmpty(name) && !name.Equals(baseIdent, StringComparison.OrdinalIgnoreCase))
        {
            return TitleCase(name);
        }

        if (baseIdent.Length >= 4)
        {
            return TitleCase(baseIdent);
        }

        return string.Join(" ", baseIdent.Select(c => NatoPhonetics.Word(c) ?? c.ToString()));
    }

    /// <summary>"BROOKMANS PARK" → "Brookmans Park"; an already mixed-case name is kept.</summary>
    public static string TitleCase(string name)
    {
        var trimmed = name.Trim();
        return trimmed.Any(char.IsLower)
            ? trimmed
            : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(trimmed.ToLowerInvariant());
    }
}
