namespace ProsimCompanion.Speech.Callouts;

/// <summary>Digit-word rendering and the closed digit grammar for spoken aviation numbers
/// (rules carried from Prosim2FO). The TTS-side normaliser that used to live beside this
/// class is Core's <see cref="Core.Speech.Phraseology"/> (issue #200).</summary>
public static class Aviation
{
    /// <summary>ICAO digit words — note 9 is "niner". Aliases the canonical Core table so the
    /// recognition grammars here and Core's identifier phonetics can never drift apart.</summary>
    public static readonly string[] DigitWords = Core.Speech.NatoPhonetics.DigitWords;

    /// <summary>"1013" → "one zero one three"; "29.92" → "two niner decimal niner two".
    /// Non-digit characters other than '.' are silently dropped.</summary>
    public static string ToDigits(string s) => Core.Speech.Phraseology.Digits(s);

    /// <summary>Words a closed digit grammar accepts (digit words + decimal/point).</summary>
    public static IReadOnlyList<string> NumberGrammarWords { get; } =
        [.. DigitWords, "decimal", "point"];

    /// <summary>"one zero one three" → 1013; "two niner decimal niner two" → 29.92. Strict:
    /// the WHOLE utterance must be a number (use NumberExtractor for embedded numbers).</summary>
    public static bool TryParseSpoken(string spoken, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(spoken))
        {
            return false;
        }

        var digits = new System.Text.StringBuilder();
        foreach (var word in spoken.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = Array.IndexOf(DigitWords, word.ToLowerInvariant());
            if (index >= 0)
            {
                digits.Append((char)('0' + index));
                continue;
            }

            if (word.Equals("decimal", StringComparison.OrdinalIgnoreCase)
                || word.Equals("point", StringComparison.OrdinalIgnoreCase))
            {
                digits.Append('.');
                continue;
            }

            return false; // a non-number word → not a pure number
        }

        return digits.Length > 0 && double.TryParse(
            digits.ToString(), System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out value);
    }
}
