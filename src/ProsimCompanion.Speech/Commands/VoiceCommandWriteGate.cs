namespace ProsimCompanion.Speech.Commands;

/// <summary>
/// Code-level bound on what a user-editable commands.json may write. The predecessor let the
/// file BE the allow-list; here the file only narrows within this fixed surface, keeping the
/// "explicit code-level allow-list" write-safety rule intact for file-driven commands: FCU
/// push-buttons and MCDU keys only — momentary switches where a stray press is recoverable.
/// Analog values, doors, fuel and everything else stay unreachable from the file no matter
/// what it says. (ProsimWriteGate in ProsimCompanion.Prosim enforces its own list underneath;
/// this gate is the feature-level narrowing on top.)
/// </summary>
public static class VoiceCommandWriteGate
{
    private static readonly string[] AllowedPrefixes =
    [
        // FCU push-buttons (AP1/AP2/A-THR/APPR/LOC/EXPED, EFIS FD/LS…) — all momentary.
        "system.switches.S_FCU_",

        // MCDU keys, captain and F/O side. Prosim2FO drove the F/O CDU (CDU2); CDU1 is
        // allowed so a user can retarget a sequence at the captain's box.
        "system.switches.S_CDU1_KEY_",
        "system.switches.S_CDU2_KEY_",
    ];

    /// <summary>True when a configured command step may write this dataref.</summary>
    public static bool IsAllowed(string dataref)
    {
        if (string.IsNullOrWhiteSpace(dataref))
        {
            return false;
        }

        foreach (var prefix in AllowedPrefixes)
        {
            if (dataref.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
