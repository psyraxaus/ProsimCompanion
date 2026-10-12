using System.Globalization;

namespace ProsimCompanion.Core.Localization;

/// <summary>
/// Parses the browser's <c>Accept-Language</c> header into language tags, best first — the
/// first-run default before the pilot picks a language on Settings → Appearance (ADR-0014).
/// Pure, so the quality-weight ordering is testable without an HTTP request.
/// </summary>
public static class AcceptLanguage
{
    /// <summary>Tags in preference order (<c>q</c> descending, header order on ties), lower-cased,
    /// wildcards and malformed entries dropped. Null or empty gives an empty list.</summary>
    public static IReadOnlyList<string> Parse(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return [];
        }

        var entries = new List<(string Tag, double Quality, int Order)>();
        var order = 0;
        foreach (var part in header.Split(','))
        {
            var pieces = part.Split(';', StringSplitOptions.TrimEntries);
            var tag = pieces[0].ToLowerInvariant();
            if (tag.Length == 0 || tag == "*")
            {
                continue;
            }

            var quality = 1.0;
            foreach (var parameter in pieces.Skip(1))
            {
                if (parameter.StartsWith("q=", StringComparison.OrdinalIgnoreCase)
                    && double.TryParse(parameter[2..], NumberStyles.Float, CultureInfo.InvariantCulture, out var q))
                {
                    quality = q;
                }
            }

            if (quality > 0)
            {
                entries.Add((tag, quality, order++));
            }
        }

        return entries
            .OrderByDescending(e => e.Quality)
            .ThenBy(e => e.Order)
            .Select(e => e.Tag)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
}
