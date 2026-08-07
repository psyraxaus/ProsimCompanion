using System.Text.RegularExpressions;

namespace ProsimCompanion.Core.Weather;

/// <summary>
/// Pulls the ATIS information letter out of a full ATIS broadcast text ("…information
/// Juliett…" → "J"). SayIntentions' getWX <c>atis</c> field carries the whole broadcast, not
/// the letter, so briefings and the weather page need this reduction. A value that is already
/// a single letter passes through uppercased; null when no letter can be identified.
/// </summary>
public static class AtisLetter
{
    public static string? Extract(string? atis)
    {
        if (string.IsNullOrWhiteSpace(atis))
        {
            return null;
        }

        atis = atis.Trim();
        if (atis.Length == 1 && char.IsLetter(atis[0]))
        {
            return atis.ToUpperInvariant();
        }

        // "information Juliett" / "info B" / "ATIS charlie"; fall back to the first token for
        // bodies that open with the letter itself ("Bravo. Wind two seven zero…").
        var m = Regex.Match(atis, @"\b(?:information|info|atis)\s+([A-Za-z]+)\b", RegexOptions.IgnoreCase);
        var token = m.Success ? m.Groups[1].Value : null;
        if (token is null)
        {
            var parts = atis.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            token = parts.Length > 0 ? parts[0] : null;
        }

        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        if (Phonetic.TryGetValue(token, out var letter))
        {
            return letter;
        }

        return token.Length == 1 && char.IsLetter(token[0]) ? token.ToUpperInvariant() : null;
    }

    // Both ICAO "juliett" and the common "juliet" misspelling map to J (seen in live SI data).
    private static readonly Dictionary<string, string> Phonetic = new(StringComparer.OrdinalIgnoreCase)
    {
        ["alpha"] = "A", ["bravo"] = "B", ["charlie"] = "C", ["delta"] = "D", ["echo"] = "E",
        ["foxtrot"] = "F", ["golf"] = "G", ["hotel"] = "H", ["india"] = "I", ["juliet"] = "J",
        ["juliett"] = "J", ["kilo"] = "K", ["lima"] = "L", ["mike"] = "M", ["november"] = "N",
        ["oscar"] = "O", ["papa"] = "P", ["quebec"] = "Q", ["romeo"] = "R", ["sierra"] = "S",
        ["tango"] = "T", ["uniform"] = "U", ["victor"] = "V", ["whiskey"] = "W", ["xray"] = "X",
        ["yankee"] = "Y", ["zulu"] = "Z",
    };
}
