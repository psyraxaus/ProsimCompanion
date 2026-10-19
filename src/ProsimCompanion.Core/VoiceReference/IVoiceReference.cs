namespace ProsimCompanion.Core.VoiceReference;

/// <summary>Who answers a spoken phrase — drives the little speaker mark on each row.</summary>
public enum VoiceSpeaker
{
    FirstOfficer,
    GroundCrew,
    Purser,
    Company,
    Atc,
}

/// <summary>The drawer's five tabs (approved design 2026-09-27, canvas draft e2a99413 v3).</summary>
public enum VoiceReferenceTab
{
    FirstOfficer,
    Ground,
    Cabin,
    Atc,
    Checklists,
}

/// <summary>
/// A UI text of the drawer named by its language-pack id (ADR-0014), not by English words:
/// the speech pillar knows WHICH text a row needs, the web page knows the pilot's language.
/// <paramref name="Arguments"/> fill the text's <c>{0}</c>-style placeholders (a checklist
/// name, an option path, a station) and are data, never translated. The phrases a row shows
/// — what the pilot says and what the FO answers — are deliberately NOT such texts: the voice
/// First Officer stays English (ADR-0014).
/// </summary>
public sealed record VoiceReferenceText(string TextId, IReadOnlyList<object>? Arguments = null)
{
    public IReadOnlyList<object> Arguments { get; init; } = Arguments ?? [];
}

/// <summary>One row of the reference: the phrase alternatives the pilot can say (joined by
/// an italic "or" on the page), what happens (<paramref name="What"/>, a pack text; null for a
/// live phrase nobody described), and what comes back. <paramref name="ValueHint"/> is the
/// shape of the value a value-parsed phrase takes ("‹120›"), shown after the phrase.
/// <paramref name="Badges"/> are the short caps markers: PF (FO must be pilot flying), INT /
/// CAB (ACP channel), PHASE (phase-gated), VALUE, DIALOGUE (opens a listening window).</summary>
public sealed record VoiceReferenceEntry(
    IReadOnlyList<string> Phrases,
    VoiceReferenceText? What,
    string? Answer = null,
    VoiceSpeaker Speaker = VoiceSpeaker.FirstOfficer,
    IReadOnlyList<string>? Badges = null,
    string? ValueHint = null)
{
    public IReadOnlyList<string> Badges { get; init; } = Badges ?? [];
}

/// <summary>One group of rows (a voice feature, or a synthetic group such as the checklist
/// starts). A switched-off feature still appears — dimmed, with <see cref="DisabledReason"/>
/// naming the setting — so the pilot learns what they could turn on.</summary>
public sealed record VoiceReferenceGroup(
    string Id,
    VoiceReferenceText Title,
    VoiceReferenceTab Tab,
    VoiceSpeaker Speaker,
    bool Enabled,
    VoiceReferenceText? DisabledReason,
    IReadOnlyList<VoiceReferenceEntry> Entries);

/// <summary>Everything the drawer renders, built live from the recognition grammar.</summary>
public sealed record VoiceReferenceSnapshot(
    IReadOnlyList<VoiceReferenceGroup> Groups,
    IReadOnlyList<VoiceReferenceEntry> AlwaysAvailable,
    string PttBindingText)
{
    public static VoiceReferenceSnapshot Empty { get; } = new([], [], "not bound");
}

/// <summary>
/// The "what can I say?" seam (issue #136): the speech pillar composes the reference from the
/// registered voice features' LIVE phrases and enabled state, the checklist start phrases,
/// the drill triggers, commands.json and atc-requests.json — never from a hand-typed list, so
/// the drawer cannot drift from the grammar the recognizer actually listens for. Kept in Core
/// so the Web project (which references only Core) can render it.
/// </summary>
public interface IVoiceReference
{
    /// <summary>Builds a fresh snapshot (cheap — a few hundred strings); called on each open.</summary>
    VoiceReferenceSnapshot Build();
}
