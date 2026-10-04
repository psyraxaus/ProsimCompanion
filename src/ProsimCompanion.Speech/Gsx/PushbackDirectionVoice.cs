using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Airports.Parking;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Gsx;

/// <summary>
/// Pure: the pilot's pushback phrases → a <see cref="PushbackChoice"/>. The real-world
/// workflow (2026-10-04): ATC says "push and start approved, face north"; the captain relays
/// it to the FO — "push back facing north", "tail left", "straight back". Compass wishes
/// are matched to a stand's routes by the advisor; left/right/straight pick GSX's slots.
/// </summary>
public static class PushbackPhraseParser
{
    private static readonly string[] Prefixes = ["push back", "pushback"];
    private static readonly string[] CompassWords =
        ["north", "north east", "east", "south east", "south", "south west", "west", "north west"];

    /// <summary>Every exact phrase the feature answers to (the closed grammar).</summary>
    public static IReadOnlyList<string> Phrases { get; } = Build();

    /// <summary>"Which way is the pushback" style questions.</summary>
    public static IReadOnlyList<string> QueryPhrases { get; } =
        ["which way is the pushback", "pushback direction", "which way do we push", "confirm pushback direction"];

    private static List<string> Build()
    {
        var list = new List<string>();
        foreach (var prefix in Prefixes)
        {
            list.Add($"{prefix} tail left");
            list.Add($"{prefix} tail right");
            list.Add($"{prefix} straight");
            list.Add($"{prefix} nose left");
            list.Add($"{prefix} nose right");
            foreach (var compass in CompassWords)
            {
                list.Add($"{prefix} facing {compass}");
            }
        }

        list.Add("tail left");
        list.Add("tail right");
        list.Add("straight back");
        list.Add("straight pushback");
        foreach (var compass in CompassWords)
        {
            list.Add($"facing {compass}");
        }

        return list;
    }

    /// <summary>The wish in the text, or null when it is not a pushback phrase. "nose left" is
    /// GSX's RIGHT slot (Nose Left/Tail Right) and "nose right" its LEFT slot.</summary>
    public static PushbackChoice? Parse(string? utterance, string source = "voice")
    {
        if (string.IsNullOrWhiteSpace(utterance))
        {
            return null;
        }

        var text = CommandMatcher.Normalize(utterance);
        var reason = $"pilot said '{utterance.Trim()}'";
        if (text.Contains("tail left", StringComparison.Ordinal) || text.Contains("nose right", StringComparison.Ordinal))
        {
            return PushbackChoice.TailLeft(source, reason);
        }

        if (text.Contains("tail right", StringComparison.Ordinal) || text.Contains("nose left", StringComparison.Ordinal))
        {
            return PushbackChoice.TailRight(source, reason);
        }

        if (text.Contains("straight", StringComparison.Ordinal))
        {
            return PushbackChoice.Straight(source, reason);
        }

        if (text.Contains("facing", StringComparison.Ordinal) && Compass.Parse(text) is { } bearing)
        {
            return PushbackChoice.Facing(bearing, source, reason);
        }

        return null;
    }

    /// <summary>What the FO says back: "Pushback tail left." / "Pushback facing north — that
    /// is tail right here." / "Pushback facing north — no route faces north at this stand;
    /// the GSX menu is yours."</summary>
    public static string Readback(PushbackChoice choice, IReadOnlyList<PushbackOption> options)
    {
        ArgumentNullException.ThrowIfNull(choice);
        ArgumentNullException.ThrowIfNull(options);
        var head = $"Pushback {choice.Spoken}";
        if (options.Count == 0)
        {
            return head + ".";
        }

        var match = PushbackAdvisor.Match(options, choice);
        if (match is null)
        {
            return choice.Wish == PushbackWish.Heading
                ? $"{head} — no route faces {Compass.Name(choice.HeadingDeg!.Value)} at this stand; the GSX menu is yours."
                : $"{head} — GSX does not offer that here; the GSX menu is yours.";
        }

        return match.Kind switch
        {
            PushbackOptionKind.Left when choice.Wish == PushbackWish.Heading => $"{head} — that is tail left here.",
            PushbackOptionKind.Right when choice.Wish == PushbackWish.Heading => $"{head} — that is tail right here.",
            PushbackOptionKind.Additional => $"{head} — {match.Label}.",
            _ => head + ".",
        };
    }

    /// <summary>The answer to "which way is the pushback".</summary>
    public static string Answer(PushbackChoiceSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Choice is { } choice)
        {
            return $"Pushback {choice.Spoken}, your call.";
        }

        if (state.Suggestion is { } suggestion)
        {
            var word = suggestion.Option.Kind switch
            {
                PushbackOptionKind.Left => "tail left",
                PushbackOptionKind.Right => "tail right",
                PushbackOptionKind.Straight => "straight back",
                _ => suggestion.Option.Label,
            };
            var runway = state.Runway is { Length: > 0 } r ? $" for runway {r}" : "";
            return suggestion.Confidence == PushbackConfidence.High
                ? $"I suggest {word}{runway} — it faces the runway."
                : $"Either way works{runway}; I lean {word}. Your call.";
        }

        return "No pushback direction yet — tell me tail left, tail right, or which way to face.";
    }
}

/// <summary>
/// Voice feature: the pilot's pushback direction for this flight ("push back facing north",
/// "tail left", "straight back") and the "which way is the pushback" query. Writes the
/// per-flight <see cref="PushbackChoiceStore"/>; the GSX pillar applies it when GSX asks.
/// Gated by <see cref="GsxOptions.VoiceControlEnabled"/>.
/// </summary>
public sealed class PushbackDirectionVoiceFeature : IVoiceFeature
{
    private const string VoiceTag = "gsx.pushback.voice";

    private readonly PushbackChoiceStore _store;
    private readonly ISpeechArbiter _arbiter;
    private readonly IOptionsMonitor<GsxOptions> _options;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<PushbackDirectionVoiceFeature> _logger;

    public PushbackDirectionVoiceFeature(
        PushbackChoiceStore store,
        ISpeechArbiter arbiter,
        IOptionsMonitor<GsxOptions> options,
        JsonlEventLog eventLog,
        ILogger<PushbackDirectionVoiceFeature> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _arbiter = arbiter;
        _options = options;
        _eventLog = eventLog;
        _logger = logger;
    }

    public bool Enabled => _options.CurrentValue.VoiceControlEnabled;

    public IEnumerable<string> Phrases => PushbackPhraseParser.Phrases.Concat(PushbackPhraseParser.QueryPhrases);

    public bool ValueParse => false;

    public bool TryHandle(string utterance)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(utterance))
        {
            return false;
        }

        var text = CommandMatcher.Normalize(utterance);
        if (PushbackPhraseParser.QueryPhrases.Any(q => CommandMatcher.Normalize(q) == text))
        {
            Speak(PushbackPhraseParser.Answer(_store.Snapshot()));
            return true;
        }

        if (!PushbackPhraseParser.Phrases.Any(p => CommandMatcher.Normalize(p) == text))
        {
            return false;
        }

        var choice = PushbackPhraseParser.Parse(utterance);
        if (choice is null)
        {
            return false;
        }

        Apply(choice);
        return true;
    }

    /// <summary>Stores the choice and reads it back. Shared with the FO's question dialogue.</summary>
    internal void Apply(PushbackChoice choice)
    {
        _store.Choose(choice);
        var state = _store.Snapshot();
        var readback = PushbackPhraseParser.Readback(choice, state.Options);
        _logger.LogInformation("Pushback choice by voice: {Choice} — {Readback}", choice.Spoken, readback);
        _eventLog.Record("pushback-choice", new { choice = choice.Spoken, choice.Source, choice.Reason, readback });
        Speak(readback);
    }

    private void Speak(string text)
        => _ = _arbiter.EnqueueAsync(new SpeechRequest(text, SpeechPriority.Normal, Ttl: TimeSpan.FromMinutes(1), Tag: VoiceTag));
}
