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

    // Hold/Standby/Resume/Continue are declared for the hold/resume feature (a tracked Phase 5
    // leftover) but stay OUT of the grammar until routed: a biased-toward phrase with no
    // handler turns "continue" into a failed item answer and a "Didn't catch that." loop.
    public static readonly IReadOnlyList<string> All =
    [
        Restart, Cancel, SayAgain, Repeat, Skip, SkipItem,
    ];
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

/// <summary>Round-robin acknowledgement phrase pools (shipped phrases; a config/phrases.json
/// override can arrive later with the persona layer).</summary>
public sealed class PhraseBank
{
    private readonly string[] _areYouSure =
        ["Are you sure?", "Confirm that?", "Double-check that one?", "Say again — that doesn't look set."];

    private readonly string[] _didNotCatch = ["Say again?", "Didn't catch that.", "Repeat please."];

    private int _areYouSureIndex;
    private int _didNotCatchIndex;

    public string NextAreYouSure()
        => _areYouSure[Interlocked.Increment(ref _areYouSureIndex) % _areYouSure.Length];

    public string NextDidNotCatch()
        => _didNotCatch[Interlocked.Increment(ref _didNotCatchIndex) % _didNotCatch.Length];
}
