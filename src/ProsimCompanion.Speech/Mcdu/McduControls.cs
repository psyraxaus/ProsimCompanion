namespace ProsimCompanion.Speech.Mcdu;

/// <summary>
/// The MCDU dataref surface: the read-only CDU2 display and the writable CDU2 keypad.
/// Like <c>FcuControls</c>/<c>RadioControls</c> this is a fixed, code-level allow-list so no
/// configuration reload can ever widen what the MCDU features may press. The captain's CDU1
/// is deliberately excluded — the FO only ever touches its own box — and keys are only ever
/// pressed momentarily (via <c>IProsimDataRefs.PressMomentaryAsync</c>, which serializes
/// presses and owns the 1 → hold → 0 → gap timing).
/// </summary>
public static class McduControls
{
    // The read-only CDU2 display ref is the typed ProsimDataRefNames.Mcdu2Display descriptor
    // (#83) — an XML document (<root><title/><line/>×12<scratchpad/></root>) parsed by
    // McduDisplayParser.

    /// <summary>Dataref prefix for the FO's CDU2 keypad
    /// (<c>system.switches.S_CDU2_KEY_*</c>: LSK1L–6R, 0–9, A–Z, FPLN/DIR/INIT/DATA/PERF/
    /// PROG/RAD_NAV/SEC_FPLN/MENU/AIRPORT, arrows, CLEAR, DOT/SLASH/SPACE/MINUS/OVFLY).
    /// Nothing outside this prefix may be written as an MCDU key.</summary>
    public const string KeyPrefix = "system.switches.S_CDU2_KEY_";

    /// <summary>Full dataref name for a CDU2 key suffix (e.g. "FPLN", "LSK1R", "1", "DOT").
    /// Throws when the suffix is empty or contains anything outside A–Z, 0–9 and '_' — the
    /// prefix plus this charset check is what makes the allow-list unescapable.</summary>
    public static string Key(string suffix)
    {
        if (!IsValidSuffix(suffix))
        {
            throw new ArgumentException($"'{suffix}' is not a valid CDU2 key suffix.", nameof(suffix));
        }

        return KeyPrefix + suffix;
    }

    /// <summary>True when <paramref name="suffix"/> is a plausible CDU2 key suffix
    /// (non-empty, upper-case letters/digits/underscore only).</summary>
    public static bool IsValidSuffix(string? suffix)
        => !string.IsNullOrEmpty(suffix)
            && suffix.All(c => c is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_');

    /// <summary>Key suffix for one typed character, or null when the character has no CDU2
    /// key (the caller refuses the whole entry rather than typing a mangled string).</summary>
    public static string? KeyForChar(char c)
    {
        if (c is >= '0' and <= '9')
        {
            return c.ToString();
        }

        var upper = char.ToUpperInvariant(c);
        if (upper is >= 'A' and <= 'Z')
        {
            return upper.ToString();
        }

        return c switch
        {
            '.' => "DOT",
            '/' => "SLASH",
            ' ' => "SPACE",
            '-' => "MINUS",
            _ => null,
        };
    }
}
