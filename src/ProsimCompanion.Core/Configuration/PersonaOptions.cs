namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// The FO persona (Prosim2FO's persona pillar): wording and tone only — the styling layer must
/// NEVER change an operational fact, number, identifier, or callout. Off by default; with the
/// LLM disabled the only observable effect is acknowledgement variation.
/// </summary>
public sealed class PersonaOptions : IOptionSection
{
    public static string SectionName => "persona";

    public bool Enabled { get; set; }

    /// <summary>FO's name, woven into the LLM style prompt ("Your name is …"); empty = unnamed.
    /// With the LLM off this has no observable effect (predecessor behaviour).</summary>
    public string Name { get; set; } = "";

    /// <summary>"junior" | "standard" | "senior" — experience colour in the style prompt.</summary>
    public string Experience { get; set; } = "standard";

    /// <summary>"casual" | "standard" | "formal" — manner colour in the style prompt.</summary>
    public string Formality { get; set; } = "standard";

    /// <summary>0–3. 0 keeps everything economical; higher allows natural warmth. "Quiet
    /// please" silences chatter for the session regardless of this value.</summary>
    public int Chattiness { get; set; }

    /// <summary>Let the persona colour the LLM-composed briefings.</summary>
    public bool StyleBriefings { get; set; } = true;

    /// <summary>Let the persona colour the LLM-styled debrief.</summary>
    public bool StyleDebrief { get; set; } = true;

    /// <summary>Restyle flow/weather advisories through the LLM (verified, template floor).</summary>
    public bool StyleAdvisories { get; set; } = true;

    /// <summary>Vary the "are you sure" / "say again" acknowledgements (random pick from the
    /// phrases.json pools instead of round-robin). Unlike the predecessor — whose persona pools
    /// were hardcoded and silently shadowed the user's phrases.json — both modes here draw from
    /// the same file.</summary>
    public bool VaryAcknowledgements { get; set; } = true;

    /// <summary>Budget for one advisory restyle round-trip; floored at 500 ms at use. The
    /// deterministic text always speaks when the budget runs out.</summary>
    public int StyleTimeoutMs { get; set; } = 2500;
}
