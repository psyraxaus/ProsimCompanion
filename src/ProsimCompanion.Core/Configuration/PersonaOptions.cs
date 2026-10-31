namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// The FO persona (Prosim2FO's persona pillar): wording and tone only — the styling layer must
/// NEVER change an operational fact, number, identifier, or callout. Off by default; with the
/// LLM disabled the only observable effect is acknowledgement variation.
/// </summary>
public sealed class PersonaOptions : IOptionSection
{
    public static string SectionName => "persona";

    [OptionField(FieldKind.Bool, "speechSettings.persona.enabled", Label = "speechSettings.common.enabled")]
    public bool Enabled { get; set; }

    /// <summary>FO's name, woven into the LLM style prompt ("Your name is …"); empty = unnamed.
    /// With the LLM off this has no observable effect (predecessor behaviour).</summary>
    [OptionField(FieldKind.Text, "speechSettings.persona.name", DependsOn = "Enabled", Normalize = FieldNormalization.Trim)]
    public string Name { get; set; } = "";

    /// <summary>"junior" | "standard" | "senior" — experience colour in the style prompt.</summary>
    [OptionField(FieldKind.Select, "speechSettings.persona.experience", Choices = "personaExperience", DependsOn = "Enabled", Normalize = FieldNormalization.TrimLower)]
    public string Experience { get; set; } = "standard";

    /// <summary>"casual" | "standard" | "formal" — manner colour in the style prompt.</summary>
    [OptionField(FieldKind.Select, "speechSettings.persona.formality", Choices = "personaFormality", DependsOn = "Enabled", Normalize = FieldNormalization.TrimLower)]
    public string Formality { get; set; } = "standard";

    /// <summary>0–3. 0 keeps everything economical; higher allows natural warmth. "Quiet
    /// please" silences chatter for the session regardless of this value.</summary>
    [OptionField(FieldKind.Number, "speechSettings.persona.chattiness", Min = "0", Max = "3", DependsOn = "Enabled")]
    public int Chattiness { get; set; }

    /// <summary>"nervous" | "steady" | "assured" — the fourth persona axis (issue #194):
    /// how sure of themselves the FO sounds. Steady adds no clause at all, so an existing
    /// persona reads exactly as before. Wording and tone only, never a fact.</summary>
    [OptionField(FieldKind.Select, "speechSettings.persona.confidence", Choices = "personaConfidence", DependsOn = "Enabled", Normalize = FieldNormalization.TrimLower)]
    public string Confidence { get; set; } = "steady";

    /// <summary>The FO roster (issue #194): a different First Officer per duty day, drawn
    /// from <c>fo-roster.json</c>. Off = this class alone describes the FO, as before.</summary>
    public PersonaRosterOptions Roster { get; set; } = new();

    /// <summary>Let the persona colour the LLM-composed briefings.</summary>
    [OptionField(FieldKind.Bool, "speechSettings.persona.styles.briefings", DependsOn = "Enabled")]
    public bool StyleBriefings { get; set; } = true;

    /// <summary>Let the persona colour the LLM-styled debrief.</summary>
    [OptionField(FieldKind.Bool, "speechSettings.persona.styles.debrief", DependsOn = "Enabled")]
    public bool StyleDebrief { get; set; } = true;

    /// <summary>Restyle flow/weather advisories through the LLM (verified, template floor).</summary>
    [OptionField(FieldKind.Bool, "speechSettings.persona.styles.advisories", DependsOn = "Enabled")]
    public bool StyleAdvisories { get; set; } = true;

    /// <summary>Vary the "are you sure" / "say again" acknowledgements (random pick from the
    /// phrases.json pools instead of round-robin). Unlike the predecessor — whose persona pools
    /// were hardcoded and silently shadowed the user's phrases.json — both modes here draw from
    /// the same file.</summary>
    [OptionField(FieldKind.Bool, "speechSettings.persona.styles.vary", DependsOn = "Enabled")]
    public bool VaryAcknowledgements { get; set; } = true;

    /// <summary>Budget for one advisory restyle round-trip; floored at 500 ms at use. The
    /// deterministic text always speaks when the budget runs out.</summary>
    [OptionField(FieldKind.Number, "speechSettings.persona.styles.budget", Suffix = "ms", Min = "500", Step = "100", DependsOn = "Enabled; StyleAdvisories", Advanced = true)]
    public int StyleTimeoutMs { get; set; } = 2500;
}

/// <summary>
/// The FO roster block of the persona section (<c>persona.roster</c>, issue #194). With the
/// roster on, the drawn FO's name, experience, formality, chattiness and confidence replace the
/// <see cref="PersonaOptions"/> values; the style switches and the restyle budget stay global.
/// </summary>
public sealed class PersonaRosterOptions
{
    public const string ModeOff = "off";
    public const string ModeDaily = "daily";
    public const string ModePinned = "pinned";

    /// <summary>"off" (default — <see cref="PersonaOptions"/> alone describes the FO) |
    /// "daily" (a new FO is drawn per duty day) | "pinned" (the FO pinned on the Roster card
    /// flies every day until released).</summary>
    [OptionField(FieldKind.Select, "speechSettings.persona.roster.mode", Choices = "personaRosterMode", DependsOn = "../Enabled", Normalize = FieldNormalization.TrimLower)]
    public string Mode { get; set; } = ModeOff;

    /// <summary>The FO introduces themselves once per duty day, on the first Preflight after
    /// the draw ("Morning captain, Sam, I'll be your First Officer today…"). Low priority.</summary>
    [OptionField(FieldKind.Bool, "speechSettings.persona.roster.gateIntro", DependsOn = "../Enabled; !Mode=off")]
    public bool GateIntro { get; set; } = true;

    /// <summary>True when the roster decides who the FO is (persona on, mode daily or pinned).</summary>
    public bool IsActive(bool personaEnabled)
        => personaEnabled && !string.IsNullOrWhiteSpace(Mode)
            && !string.Equals(Mode.Trim(), ModeOff, StringComparison.OrdinalIgnoreCase);

    /// <summary>True in pinned mode (the pinned FO flies; nothing is drawn).</summary>
    public bool IsPinned => string.Equals(Mode?.Trim(), ModePinned, StringComparison.OrdinalIgnoreCase);
}
