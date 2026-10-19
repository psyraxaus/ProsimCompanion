using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.VoiceReference;

namespace ProsimCompanion.Speech.Recognition;

/// <summary>
/// What a voice feature declares about itself, once, beside its own code (candidate 5 of the
/// 2026-10 architecture review): where it stands in the dispatch order, which option switches
/// it on, and how the Voice Reference drawer describes it. Before this record the same three
/// facts lived in three mirrors — the DI registration order, the router's #137 ordering pass
/// and a describer dictionary keyed by type — and a phrase renamed in a feature silently
/// dropped its drawer row. The declaration is the feature's interface to the
/// <see cref="VoiceFeatureRoster"/>: the router, the drawer and the DI consume the roster and
/// hold no ordering knowledge of their own.
/// </summary>
/// <remarks>
/// Convention: a <c>public static VoiceFeatureDeclaration Declared { get; }</c> on the feature
/// class, returned from <see cref="IVoiceFeature.Declaration"/>. The static form is what lets
/// <c>VoiceFeatureRosterTests</c> pin the whole order and check every text id against the
/// English pack without standing up thirty services and their dependencies.
/// </remarks>
public sealed record VoiceFeatureDeclaration
{
    /// <summary>Dispatch rank — the lower, the earlier the router offers an utterance to the
    /// feature. Explicit and unique per shipped feature (the roster test pins every value), ten
    /// apart so a new feature slots between its neighbours without renumbering. Equal values
    /// keep registration order; only test fakes rely on that.</summary>
    public required int Precedence { get; init; }

    /// <summary>The option that switches the feature on, as the drawer names it while off.</summary>
    public required VoiceFeatureGate Gate { get; init; }

    /// <summary>How the drawer describes the feature: described rows, or live composition.</summary>
    public required VoiceReferenceDescription Description { get; init; }
}

/// <summary>
/// The name of a feature's enabling option, for the drawer's dimmed "… is off — Settings → …"
/// line. <see cref="IVoiceFeature.Enabled"/> stays the live predicate; the gate is what the
/// pilot reads when that predicate is false. <paramref name="Option"/> is the settings.json
/// path (data, fills the text's <c>{0}</c>); <paramref name="OffTextId"/> is the pack text that
/// names the settings page. Null for both = always on, the drawer never dims the group.
/// </summary>
public sealed record VoiceFeatureGate(string? Option, string? OffTextId)
{
    /// <summary>A feature with no switch — <see cref="IVoiceFeature.Enabled"/> is always true.</summary>
    public static VoiceFeatureGate AlwaysOn { get; } = new(null, null);

    /// <summary>A feature behind a settings.json option.</summary>
    public static VoiceFeatureGate ByOption(string option, string offTextId) => new(option, offTextId);

    /// <summary>A feature gated by something that is not a setting (a pillar that is not
    /// running); the text says what is missing.</summary>
    public static VoiceFeatureGate ByRuntime(string offTextId) => new(null, offTextId);

    public bool IsAlwaysOn => OffTextId is null;

    /// <summary>The drawer's reason while the feature is off, with the option path as <c>{0}</c>;
    /// null for an always-on feature.</summary>
    public VoiceReferenceText? OffReason
        => OffTextId is null ? null : new VoiceReferenceText(OffTextId, Option is null ? null : [Option]);
}

/// <summary>How a feature appears in the Voice Reference drawer — one of the two sealed
/// shapes below. A closed hierarchy, so the builder's switch is exhaustive.</summary>
public abstract record VoiceReferenceDescription;

/// <summary>
/// The usual shape: a titled group of described rows. The rows DESCRIBE the live grammar,
/// they are never its source — the builder keeps a row only while the feature still
/// contributes its phrases, and lists a live phrase the rows do not know undescribed. All
/// wording is pack text ids (ADR-0014); the phrases and the FO's answers stay English.
/// </summary>
public sealed record DescribedVoiceReference(
    string Id,
    string TitleTextId,
    VoiceReferenceTab Tab,
    VoiceSpeaker Speaker,
    IReadOnlyList<VoiceReferenceRow> Rows) : VoiceReferenceDescription;

/// <summary>Composes a feature's drawer groups from its own live data (GSX bindings, the hail
/// lists, commands.json, atc-requests.json); <paramref name="livePhrases"/> is the feature's
/// current grammar, trimmed and de-duplicated, <paramref name="context"/> the live options the
/// wording depends on.</summary>
public delegate IEnumerable<VoiceReferenceGroup> VoiceReferenceComposer(
    IVoiceFeature feature,
    IReadOnlyList<string> livePhrases,
    VoiceReferenceContext context);

/// <summary>The shape for a feature whose rows come from data that changes at runtime, so no
/// static row list could describe it; the composer lives beside the feature.</summary>
public sealed record ComposedVoiceReference(VoiceReferenceComposer Compose) : VoiceReferenceDescription;

/// <summary>One described row: the phrase names as the feature contributes them (a name also
/// matches live phrases that start with it plus a space — the value-parsed seeds and the
/// per-airport logbook phrases), the pack text id of what happens, the FO's answer (English,
/// spoken), the caps badges, the value shape and an optional speaker other than the group's.</summary>
public sealed record VoiceReferenceRow(
    string[] Phrases,
    string WhatTextId,
    string? Answer = null,
    string[]? Badges = null,
    string? ValueHint = null,
    VoiceSpeaker? Speaker = null);

/// <summary>The live options a composed description may read — handed in by the builder so a
/// composer stays a pure function of feature + options and needs no injection of its own.</summary>
public sealed record VoiceReferenceContext(SpeechOptions Speech, GroundCrewOptions GroundCrew, CabinOptions Cabin);

/// <summary>The drawer's short caps markers, as the approved design names them.</summary>
public static class VoiceBadges
{
    /// <summary>The FO must be pilot flying.</summary>
    public const string Pf = "PF";

    /// <summary>Answered on the interphone channel of the audio panel (the badge reads INT).</summary>
    public const string Interphone = "INT";

    /// <summary>Answered on the cabin channel of the audio panel.</summary>
    public const string Cab = "CAB";

    /// <summary>Phase-gated.</summary>
    public const string Phase = "PHASE";

    /// <summary>Takes a spoken value.</summary>
    public const string Value = "VALUE";

    /// <summary>Opens a listening window.</summary>
    public const string Dialogue = "DIALOGUE";
}

/// <summary>The pack text ids every declaration shares, so a typo is one place, not thirty.</summary>
public static class VoiceReferenceTextIds
{
    /// <summary>"{0} is off — Settings → Voice First Officer".</summary>
    public const string OffSpeechFo = "voiceReference.off.speechFo";

    /// <summary>"{0} is off — Settings → Voice First Officer → MCDU".</summary>
    public const string OffMcdu = "voiceReference.off.mcdu";

    /// <summary>"{0} is off — Settings → Voice First Officer → Callouts &amp; Placards → In-flight monitoring".</summary>
    public const string OffMonitoring = "voiceReference.off.monitoring";

    /// <summary>"{0} is off — Settings → GSX".</summary>
    public const string OffGsxSettings = "voiceReference.off.gsxSettings";

    /// <summary>"{0} is off — Settings → Ground Services".</summary>
    public const string OffGroundServices = "voiceReference.off.groundServices";

    /// <summary>"{0} is off" — the hails, whose own sub-gates add the page.</summary>
    public const string OffBare = "voiceReference.off.bare";

    /// <summary>"GSX pillar not running".</summary>
    public const string OffGsxPillar = "voiceReference.off.gsxPillar";

    /// <summary>"SayIntentions is off — Settings → Voice First Officer → SayIntentions".</summary>
    public const string OffSayIntentions = "voiceReference.off.sayIntentions";

    /// <summary>"switched off" — the drawer's own fallback.</summary>
    public const string SwitchedOff = "voiceReference.switchedOff";
}
