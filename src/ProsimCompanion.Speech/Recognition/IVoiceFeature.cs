namespace ProsimCompanion.Speech.Recognition;

/// <summary>
/// A voice-drivable feature (FCU, radios, role handover…) plugged into the utterance router.
/// Value-parsing features receive the RAW transcription before snapping (so "descend flight
/// level one two zero" keeps its number); the rest see interpreter-resolved text. Features
/// are consulted only when no checklist item is awaiting an answer. Where a feature stands in
/// that consultation order, which option switches it on and how the Voice Reference drawer
/// describes it are the feature's own <see cref="Declaration"/>; the
/// <see cref="VoiceFeatureRoster"/> reads it, nothing else needs to.
/// </summary>
public interface IVoiceFeature
{
    /// <summary>The feature's standing in the roster — precedence, gate and drawer rows — by
    /// convention the class's <c>public static VoiceFeatureDeclaration Declared</c>, so the
    /// roster test can read it without constructing the feature. Deliberately abstract: a
    /// feature that forgets to declare itself fails to compile, not to dispatch.</summary>
    VoiceFeatureDeclaration Declaration { get; }

    /// <summary>Whether the feature is currently switched on (campaign #82). The router owns
    /// the gate: a disabled feature contributes NO phrases to the closed grammar (a smaller
    /// grammar mishears less) and is never offered the utterance — implementations no longer
    /// need to hand-roll the check in both places. Option-backed features return their live
    /// option value; always-on features return true.</summary>
    bool Enabled { get; }

    /// <summary>Grammar phrases contributed to the idle listening window (when
    /// <see cref="Enabled"/>).</summary>
    IEnumerable<string> Phrases { get; }

    /// <summary>True to receive the raw transcription before the snapper runs.</summary>
    bool ValueParse { get; }

    /// <summary>Handles the utterance; true = consumed. Not called while disabled.</summary>
    bool TryHandle(string utterance);

    /// <summary>True for a feature that only ever answers with words — nothing it does moves a
    /// switch, a lever or a setting (the FMA / capture-call acknowledgements). The router may
    /// fire such a feature on a gray-band match without the "did you mean …?" round trip in a
    /// sterile moment (issue #177): a wrong guess costs one "Checked.", never an actuation.
    /// Default false — every feature that writes anywhere keeps the clarifier.</summary>
    bool AcknowledgeOnly => false;
}
