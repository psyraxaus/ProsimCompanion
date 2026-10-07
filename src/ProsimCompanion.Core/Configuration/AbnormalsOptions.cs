namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// ECAM abnormals and memory drills (Prosim2FO "abnormals" semantics). Detection reads the
/// E/WD text and the per-system datarefs; the interactive dialogue then walks the procedure
/// line by line with the pilot. Detect-and-report only — the FO never actuates anything.
/// Added 2026-10-08: until then the dialogue's timeouts were constants in
/// <c>EcamDialogueCore</c> and there was no switch at all (owner rule: every option has a
/// web control).
/// </summary>
public sealed class AbnormalsOptions : IOptionSection
{
    public static string SectionName => "abnormals";

    /// <summary>Master switch for failure detection and the announcements. Off: nothing is
    /// detected or announced; the memory drills stay voice-invocable as rehearsals.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Walk the ECAM procedure with the pilot line by line (confirm / verify /
    /// standby / skip). Off: the FO announces the failure ("Master caution. Hydraulic …")
    /// and leaves the ECAM to you — the announce-only behaviour of degraded mode.</summary>
    public bool InteractiveDialogue { get; set; } = true;

    /// <summary>Pilot-response window per dialogue prompt, seconds (Prosim2FO default 20,
    /// clamped to at least 3 at use).</summary>
    public int ConfirmTimeoutSeconds { get; set; } = 20;

    /// <summary>Verification mismatches tolerated before the FO offers continue / standby
    /// (Prosim2FO's <c>maxVerifyRetries</c>).</summary>
    public int MaxVerifyRetries { get; set; } = 1;

    /// <summary>Unanswered prompts in a row before the FO stops re-prompting and stands by on
    /// its own (0 = re-prompt forever, the Prosim2FO behaviour).</summary>
    public int MaxSilentPrompts { get; set; } = 3;
}
