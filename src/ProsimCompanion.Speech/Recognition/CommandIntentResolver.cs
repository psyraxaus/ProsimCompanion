using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Llm;

namespace ProsimCompanion.Speech.Recognition;

/// <summary>
/// The intent tier (owner request 2026-11-07, plan O2): when nothing in the vocabulary
/// matched a spoken request — not exactly, not by the phonetic snap — the language model is
/// asked which ONE phrase of the live vocabulary the captain meant, or none. It can only ever
/// pick a phrase the router could have fired anyway: the answer is checked against the list,
/// anything else is "none". <see cref="SpeechOptions.IntentTier"/> says whether it runs and
/// whether the pick needs a "did you mean …?" before anything that writes to the aircraft or
/// calls GSX fires.
/// </summary>
public interface ICommandIntentResolver
{
    /// <summary>off | confirm | auto — the configured tier, normalised.</summary>
    string Tier { get; }

    /// <summary>True when the tier is on, the model is configured and not known-unhealthy.</summary>
    bool Enabled { get; }

    /// <summary>The vocabulary phrase the captain meant, or null. Never throws.</summary>
    Task<string?> ResolveAsync(string rawUtterance, IReadOnlyList<string> vocabulary, CancellationToken cancellationToken);
}

/// <summary>The pure half: the prompt and the reading of the model's answer.</summary>
public static class IntentReply
{
    public const string None = "NONE";

    /// <summary>The system prompt — the model sees the list and must answer with one line of it.</summary>
    public const string System =
        "You map an airline captain's spoken request, transcribed by speech recognition, onto EXACTLY ONE phrase from a list "
        + "of commands the first officer understands. Answer with that phrase, copied character for character, and nothing else. "
        + "If no phrase has the same meaning, answer NONE. Never invent a phrase, never add words, never explain. "
        + "The transcription may have small errors; a request to start, begin, commence, call or order a ground service "
        + "maps to that service's phrase. A question is NONE. Small talk is NONE.";

    /// <summary>The user turn: the list, then what was heard.</summary>
    public static string User(string rawUtterance, IReadOnlyList<string> vocabulary)
    {
        ArgumentNullException.ThrowIfNull(rawUtterance);
        ArgumentNullException.ThrowIfNull(vocabulary);
        return "PHRASES:\n" + string.Join('\n', vocabulary.Where(v => v != NumberGrammar.Sentinel).Select(v => "- " + v))
            + "\n\nHEARD: " + rawUtterance.Trim() + "\n\nANSWER:";
    }

    /// <summary>The vocabulary phrase the reply names, or null — a reply is accepted only when
    /// it IS a phrase (quotes, a leading dash, trailing punctuation and case forgiven).</summary>
    public static string? Pick(string? reply, IReadOnlyList<string> vocabulary)
    {
        ArgumentNullException.ThrowIfNull(vocabulary);
        if (string.IsNullOrWhiteSpace(reply))
        {
            return null;
        }

        var line = reply.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
        line = line.TrimStart('-', '*', ' ').Trim().Trim('"', '\'', '`', '.', '!').Trim();
        if (line.Length == 0 || string.Equals(line, None, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var wanted = CommandMatcher.Normalize(line);
        return vocabulary.FirstOrDefault(v => v != NumberGrammar.Sentinel && CommandMatcher.Normalize(v).Equals(wanted, StringComparison.Ordinal));
    }
}

/// <summary>The resolver over <see cref="OpenAiChatClient"/>: a short budget, one call, the
/// pick checked against the list, every outcome on the session log as <c>voice.intent</c>.</summary>
public sealed class LlmCommandIntentResolver : ICommandIntentResolver
{
    public const string EventName = "voice.intent";
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(4);

    private readonly IOptionsMonitor<SpeechOptions> _options;
    private readonly OpenAiChatClient _llm;
    private readonly LlmHealthStore _health;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<LlmCommandIntentResolver> _logger;

    public LlmCommandIntentResolver(
        IOptionsMonitor<SpeechOptions> options,
        OpenAiChatClient llm,
        LlmHealthStore health,
        JsonlEventLog eventLog,
        ILogger<LlmCommandIntentResolver> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(llm);
        ArgumentNullException.ThrowIfNull(health);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _llm = llm;
        _health = health;
        _eventLog = eventLog;
        _logger = logger;
    }

    public string Tier => (_options.CurrentValue.IntentTier ?? "").Trim().ToLowerInvariant() switch
    {
        "auto" => "auto",
        "confirm" => "confirm",
        _ => "off",
    };

    public bool Enabled => Tier != "off" && _llm.IsConfigured && !_health.Snapshot().IsUnhealthy;

    public async Task<string?> ResolveAsync(string rawUtterance, IReadOnlyList<string> vocabulary, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(vocabulary);
        if (string.IsNullOrWhiteSpace(rawUtterance) || vocabulary.Count == 0 || !Enabled)
        {
            return null;
        }

        var watch = Stopwatch.StartNew();
        string? reply = null;
        string? picked = null;
        var outcome = "none";
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(Budget);
            reply = await _llm.CompleteAsync(IntentReply.System, IntentReply.User(rawUtterance, vocabulary), budget.Token).ConfigureAwait(false);
            picked = IntentReply.Pick(reply, vocabulary);
            outcome = picked is null ? (reply is null ? "no-reply" : "none") : "picked";
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            outcome = "timeout";
        }
        catch (Exception ex)
        {
            outcome = "error";
            _logger.LogDebug(ex, "Intent tier: the model call failed");
        }

        _eventLog.Record(EventName, new { heard = rawUtterance, phrase = picked, outcome, tier = Tier, ms = watch.ElapsedMilliseconds, reply = picked is null ? Truncate(reply) : null });
        _logger.LogInformation("Intent tier ({Tier}): \"{Heard}\" -> {Outcome} {Phrase} in {Ms} ms", Tier, rawUtterance, outcome, picked ?? "", watch.ElapsedMilliseconds);
        return picked;
    }

    private static string? Truncate(string? text) => text is null ? null : text.Length <= 120 ? text.Trim() : text[..120].Trim() + "…";
}
