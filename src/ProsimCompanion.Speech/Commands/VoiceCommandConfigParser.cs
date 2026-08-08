using System.Text.Json;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Commands;

/// <summary>Outcome of parsing commands.json: the immutable set plus human-readable warnings
/// for the caller to log (the parser itself never logs — it is pure and file-free).</summary>
public sealed record VoiceCommandParseResult(VoiceCommandSet Set, IReadOnlyList<string> Warnings);

/// <summary>
/// Pure JSON → <see cref="VoiceCommandSet"/> parser for the file-driven voice-command engine
/// (Prosim2FO's commands.json schema). Tolerates comments, trailing commas and "//" note
/// properties. Phrases containing '«' are placeholders and are skipped (predecessor rule);
/// commands whose steps write outside <see cref="VoiceCommandWriteGate"/> are kept in the
/// phrase map but blocked, so the FO refuses audibly rather than writing or staying silent.
/// </summary>
public static class VoiceCommandConfigParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Parses commands.json text. Throws <see cref="JsonException"/> on malformed
    /// JSON — the caller decides whether to keep a previous set.</summary>
    public static VoiceCommandParseResult Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        var file = JsonSerializer.Deserialize<VoiceCommandFile>(json, JsonOptions) ?? new VoiceCommandFile();
        var warnings = new List<string>();
        var byPhrase = new Dictionary<string, ResolvedVoiceCommand>(StringComparer.Ordinal);
        var allow = new HashSet<string>(StringComparer.Ordinal);

        foreach (var command in file.Commands)
        {
            var blockReason = BlockReason(command);
            if (blockReason is not null)
            {
                warnings.Add($"Voice command \"{command.Label}\" is blocked: {blockReason}");
            }
            else
            {
                // Runnable commands declare their step datarefs as the write allow-list —
                // the predecessor's rule, now bounded by VoiceCommandWriteGate above.
                foreach (var step in command.Steps)
                {
                    if (!IsPlaceholder(step.Dataref))
                    {
                        allow.Add(step.Dataref);
                    }
                }
            }

            foreach (var phrase in command.Phrases)
            {
                if (string.IsNullOrWhiteSpace(phrase) || phrase.Contains('«'))
                {
                    continue; // placeholder phrase — not yet filled in by the user
                }

                var key = CommandMatcher.Normalize(phrase);
                if (key.Length == 0)
                {
                    continue;
                }

                if (!byPhrase.TryAdd(key, new ResolvedVoiceCommand(command, blockReason)))
                {
                    warnings.Add($"Duplicate voice-command phrase \"{phrase}\" ignored (first definition wins)");
                }
            }
        }

        return new VoiceCommandParseResult(new VoiceCommandSet(file.Commands, byPhrase, allow), warnings);
    }

    /// <summary>Blank or «placeholder» datarefs are "not configured yet", not a write-gate
    /// violation — they refuse politely at execution time instead of blocking the command.</summary>
    public static bool IsPlaceholder(string dataref)
        => string.IsNullOrWhiteSpace(dataref) || dataref.Contains('«');

    private static string? BlockReason(VoiceCommandDefinition command)
    {
        foreach (var step in command.Steps)
        {
            if (!IsPlaceholder(step.Dataref) && !VoiceCommandWriteGate.IsAllowed(step.Dataref))
            {
                return $"dataref '{step.Dataref}' is outside the voice-command write gate " +
                       "(FCU push-buttons and MCDU keys only)";
            }
        }

        return null;
    }
}
