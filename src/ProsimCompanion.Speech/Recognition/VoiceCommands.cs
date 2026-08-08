namespace ProsimCompanion.Speech.Recognition;

/// <summary>Global checklist voice commands — always in every listening grammar. Phrases
/// carried verbatim from Prosim2FO. An awaiting item's accepted phrase outranks the
/// identically-named command.</summary>
public static class VoiceCommands
{
    public const string Restart = "restart checklist";
    public const string Cancel = "cancel checklist";
    public const string Hold = "hold the checklist";
    public const string Standby = "standby";
    public const string Resume = "resume checklist";
    public const string Continue = "continue";
    public const string SayAgain = "say again";
    public const string Repeat = "repeat";
    public const string Skip = "skip";
    public const string SkipItem = "skip item";

    public static readonly IReadOnlyList<string> All =
    [
        Restart, Cancel, SayAgain, Repeat, Skip, SkipItem, Hold, Standby, Resume, Continue,
    ];

    /// <summary>The words that resume a held checklist — the hold window listens for exactly
    /// these (plus cancel/restart) so an unrelated remark can't accidentally resume.</summary>
    public static readonly IReadOnlyList<string> ResumeWords = [Resume, Continue];
}

/// <summary>Yes/no confirmation vocabulary for gray-band "did you mean …?" dialogues.</summary>
public static class ConfirmVocabulary
{
    public static readonly IReadOnlyList<string> Affirm =
        ["affirm", "affirmative", "confirmed", "confirm", "correct", "yes"];

    public static readonly IReadOnlyList<string> Negative =
        ["negative", "no", "wrong", "incorrect", "correction"];

    public static readonly IReadOnlyList<string> SayAgain = ["say again", "repeat"];

    public static IReadOnlyList<string> All { get; } = [.. Affirm, .. Negative, .. SayAgain];
}

// PhraseBank moved to Persona/PhraseBank.cs — file-backed by config/phrases.json with the
// persona layer drawing from the same pools.
