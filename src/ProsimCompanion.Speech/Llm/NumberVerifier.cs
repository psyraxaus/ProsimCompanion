using System.Globalization;
using System.Text.RegularExpressions;

namespace ProsimCompanion.Speech.Llm;

/// <summary>One verification result: <paramref name="Ok"/> when every significant number in
/// the narrative traces to an allowed fact value; <paramref name="Offending"/> lists the
/// tokens that don't (in narrative order, duplicates preserved); <paramref name="Allowed"/>
/// echoes the allowed set for building the strict re-ask prompt.</summary>
public sealed record NumberCheck(
    bool Ok,
    IReadOnlyList<string> Offending,
    IReadOnlyList<double> Allowed);

/// <summary>
/// The conservative number guard shared by every LLM-styled composition (briefing, debrief):
/// a "significant" token — one containing a decimal point, or 3+ digits — must match an
/// allowed fact value within a small tolerance, or the narrative is rejected. One- and
/// two-digit integers (runway numbers, flap settings, ordinals) are deliberately not checked;
/// they false-positive far more than they catch. Callers own building the allowed set from
/// their fact type — this class knows nothing about facts.
/// </summary>
public static class NumberVerifier
{
    /// <summary>Match slack for float formatting drift (e.g. "110.3" vs 110.30) — well under
    /// any aeronautically meaningful difference.</summary>
    public const double Tolerance = 0.06;

    /// <summary>Checks every significant number in <paramref name="narrative"/> against
    /// <paramref name="allowed"/>.</summary>
    public static NumberCheck Check(string narrative, IReadOnlyList<double> allowed)
    {
        ArgumentNullException.ThrowIfNull(narrative);
        ArgumentNullException.ThrowIfNull(allowed);

        var offending = new List<string>();
        foreach (Match match in Regex.Matches(narrative, @"\d+(?:\.\d+)?"))
        {
            var token = match.Value;
            var significant = token.Contains('.', StringComparison.Ordinal)
                || token.Replace(".", "", StringComparison.Ordinal).Length >= 3;
            if (!significant)
            {
                continue;
            }

            var value = double.Parse(token, CultureInfo.InvariantCulture);
            if (!allowed.Any(a => Math.Abs(a - value) <= Tolerance))
            {
                offending.Add(token);
            }
        }

        return new NumberCheck(offending.Count == 0, offending, allowed);
    }

    /// <summary>The allowed set rendered for a strict re-ask prompt ("140, 110.3, …"),
    /// deduplicated after formatting.</summary>
    public static string DescribeAllowed(IReadOnlyList<double> allowed)
    {
        ArgumentNullException.ThrowIfNull(allowed);
        return string.Join(", ", allowed
            .Select(a => a.ToString("0.##", CultureInfo.InvariantCulture))
            .Distinct());
    }
}
