using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Checklists;

/// <summary>
/// Parses a spoken Airbus takeoff flap config from a checklist answer (issue #125): "config 1
/// plus F" / "one plus F" / "config two" / "flaps 3" all name a handle detent 1–3. Pure so the
/// phrasing survives regression tests; digit AND word forms are both matched because the ASR
/// emits either ("Config 1 plus F." was the flight that raised this). A generic "set"/"checked"
/// answer carries no config and returns null — the engine then cross-checks lever vs
/// performance on its own.
/// </summary>
internal static class FlapConfigAnswer
{
    /// <summary>The spoken config as a flap-handle detent (1–3), or null when the answer
    /// names none. Takeoff never uses FULL, so "full" deliberately does not parse — it will
    /// fail the read-back the way a wrong number would.</summary>
    internal static int? TryParse(string answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
        {
            return null;
        }

        var words = CommandMatcher.Normalize(answer).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var word in words)
        {
            switch (word)
            {
                case "1" or "one":
                    return 1;
                case "2" or "two":
                    return 2;
                case "3" or "three":
                    return 3;
            }
        }

        return null;
    }

    /// <summary>The config as the FO reads it back ("config one plus F"). Detent 1 is always
    /// 1+F on takeoff (slats+flaps); null/unset reads honestly rather than inventing one.</summary>
    internal static string Spoken(int? detent) => detent switch
    {
        1 => "config one plus F",
        2 => "config two",
        3 => "config three",
        4 => "config full",
        0 => "not set",
        _ => "unavailable",
    };
}
