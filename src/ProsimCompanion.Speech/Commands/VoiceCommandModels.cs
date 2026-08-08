using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Commands;

/// <summary>
/// One momentary key press inside a configured voice command: write <see cref="Press"/> →
/// hold → write <see cref="Restore"/> (when present) → wait <see cref="DelayMs"/> so the MCDU
/// can change pages before the next key. Shape mirrors Prosim2FO's commands.json steps.
/// </summary>
public sealed class VoiceCommandStep
{
    /// <summary>ProSim dataref written by this step (a momentary switch such as
    /// <c>system.switches.S_CDU2_KEY_PERF</c>).</summary>
    public string Dataref { get; set; } = "";

    /// <summary>Value written on press (1 for the momentary switches).</summary>
    public int Press { get; set; } = 1;

    /// <summary>Value written after the hold to release the key; null = fire-and-forget
    /// (no restore write).</summary>
    public int? Restore { get; set; }

    /// <summary>How long the key stays pressed before the restore write.</summary>
    public int HoldMs { get; set; } = 150;

    /// <summary>Settle time after this step (page-change time on the MCDU).</summary>
    public int DelayMs { get; set; }
}

/// <summary>
/// A file-configured voice command: trigger phrases, an optional spoken confirmation
/// (<see cref="Say"/>, supporting <c>{token}</c> placeholders), and a press sequence.
/// A command with no steps is a spoken query — it only speaks.
/// </summary>
public sealed class VoiceCommandDefinition
{
    public List<string> Phrases { get; set; } = [];

    /// <summary>Spoken after the steps complete (or immediately for a query command).
    /// May contain tokens: {altimeter} {qnh} {v1} {vr} {v2} {flex} {runway}.</summary>
    public string Say { get; set; } = "";

    public List<VoiceCommandStep> Steps { get; set; } = [];

    /// <summary>First phrase, used as the command's label in logs and refusals.</summary>
    public string Label => Phrases.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)) ?? "command";
}

/// <summary>Top-level shape of config/commands.json.</summary>
public sealed class VoiceCommandFile
{
    public List<VoiceCommandDefinition> Commands { get; set; } = [];
}

/// <summary>A phrase-resolved command plus the reason it may not execute (null = runnable).
/// Blocked commands stay in the phrase map so the FO can refuse audibly instead of staying
/// silent on a phrase the user configured.</summary>
public sealed record ResolvedVoiceCommand(VoiceCommandDefinition Definition, string? BlockReason);

/// <summary>
/// The immutable, parsed view of commands.json: phrase lookup plus the file-derived write
/// allow-list (the union of every runnable command's step datarefs — the predecessor's
/// "steps declare the allow-list" rule). Pure data; built by
/// <see cref="VoiceCommandConfigParser"/> so parsing/matching is testable without files.
/// </summary>
public sealed class VoiceCommandSet
{
    public static VoiceCommandSet Empty { get; } = new([], new Dictionary<string, ResolvedVoiceCommand>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));

    private readonly IReadOnlyDictionary<string, ResolvedVoiceCommand> _byNormalizedPhrase;

    public VoiceCommandSet(
        IReadOnlyList<VoiceCommandDefinition> commands,
        IReadOnlyDictionary<string, ResolvedVoiceCommand> byNormalizedPhrase,
        IReadOnlySet<string> writeAllowList)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(byNormalizedPhrase);
        ArgumentNullException.ThrowIfNull(writeAllowList);

        Commands = commands;
        _byNormalizedPhrase = byNormalizedPhrase;
        WriteAllowList = writeAllowList;
        Phrases = [.. byNormalizedPhrase.Values
            .SelectMany(r => r.Definition.Phrases)
            .Where(p => !string.IsNullOrWhiteSpace(p) && !p.Contains('«'))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    public IReadOnlyList<VoiceCommandDefinition> Commands { get; }

    /// <summary>Datarefs the loaded file may write — every step write is validated against
    /// this set again at execution time.</summary>
    public IReadOnlySet<string> WriteAllowList { get; }

    /// <summary>Grammar phrases (original casing) contributed to the listening window.</summary>
    public IReadOnlyList<string> Phrases { get; }

    /// <summary>Resolves an utterance (raw or interpreter-resolved) to a configured command
    /// by normalized exact match.</summary>
    public bool TryResolve(string utterance, out ResolvedVoiceCommand resolved)
    {
        ArgumentNullException.ThrowIfNull(utterance);
        return _byNormalizedPhrase.TryGetValue(CommandMatcher.Normalize(utterance), out resolved!);
    }
}
