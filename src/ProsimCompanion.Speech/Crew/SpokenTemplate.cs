using System.Text.RegularExpressions;

namespace ProsimCompanion.Speech.Crew;

/// <summary>
/// Fills a crew wording's placeholder, or drops the clause that carried it when the value is
/// unknown (owner request 2026-11-07, plan V3): "fuelling in progress, {fuel} tonnes
/// requested." without a plan reads "fuelling in progress." — never "zero tonnes" and never
/// a bare placeholder spoken aloud.
/// </summary>
internal static partial class SpokenTemplate
{
    /// <param name="template">The configured wording.</param>
    /// <param name="placeholder">"{fuel}", "{fluid}", "{pax}" — braces included.</param>
    /// <param name="value">The spoken value; null or blank drops the clause.</param>
    internal static string FillOrDrop(string template, string placeholder, string? value)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentException.ThrowIfNullOrWhiteSpace(placeholder);
        if (!template.Contains(placeholder, StringComparison.Ordinal))
        {
            return template;
        }

        if (!string.IsNullOrWhiteSpace(value))
        {
            return template.Replace(placeholder, value.Trim(), StringComparison.Ordinal);
        }

        // The clause: from the comma or space before it to the next clause boundary.
        var clause = new Regex(@"(?:,\s*|\s+)?[^,.;:!?—]*" + Regex.Escape(placeholder) + @"[^,.;:!?—]*", RegexOptions.CultureInvariant);
        var text = clause.Replace(template, "");
        text = SpaceBeforePunctuation().Replace(text, "$1");
        text = DoubleSpaces().Replace(text, " ").Trim();
        return OnlyPunctuation().IsMatch(text) ? "" : text;
    }

    [GeneratedRegex(@"\s+([.,;:!?])")]
    private static partial Regex SpaceBeforePunctuation();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex DoubleSpaces();

    [GeneratedRegex(@"^[\s\p{P}]*$")]
    private static partial Regex OnlyPunctuation();
}
