using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Hosting;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Gsx;

/// <summary>
/// The FO's "which one?" question for an arrival gate GSX split into suffix variants
/// (issue #156, 2026-10-11: EFHK W36 → 36A / 36B). The <see cref="GateChoiceStore"/> raises
/// the question; this dialogue voices it — "W36 is 36 Alpha or 36 Bravo in this scenery —
/// which?" — borrows the mic, listens for "36 alpha" / "alpha" / "the first" / "36A", stores
/// the answer and reads it back. Timeout, two misses or "your call" leave the question open
/// with a word: the GSX pillar takes the first variant at taxi-in. Modelled on
/// <see cref="PushbackQuestionDialogue"/>. Startup module: construction subscribes; the host
/// calls <see cref="Start"/>.
/// </summary>
public sealed class GateChoiceDialogue : IStartupModule, IDisposable
{
    private static readonly TimeSpan ListenTimeout = TimeSpan.FromSeconds(12);
    private const int MaxAttempts = 2;

    private static readonly IReadOnlyList<string> HandBack = ["your call", "leave it", "you pick", "cancel"];

    private static readonly IReadOnlyDictionary<char, string> Nato = new Dictionary<char, string>
    {
        ['A'] = "alpha", ['B'] = "bravo", ['C'] = "charlie", ['D'] = "delta", ['E'] = "echo", ['F'] = "foxtrot",
        ['G'] = "golf", ['H'] = "hotel", ['I'] = "india", ['J'] = "juliet", ['K'] = "kilo", ['L'] = "lima",
        ['M'] = "mike", ['N'] = "november", ['O'] = "oscar", ['P'] = "papa", ['Q'] = "quebec", ['R'] = "romeo",
        ['S'] = "sierra", ['T'] = "tango", ['U'] = "uniform", ['V'] = "victor", ['W'] = "whiskey", ['X'] = "x-ray",
        ['Y'] = "yankee", ['Z'] = "zulu",
    };

    private static readonly Regex LabelShape = new(@"^(?<number>\d{1,4})(?<suffix>[A-Za-z]{1,2})$", RegexOptions.Compiled);

    private readonly GateChoiceStore _store;
    private readonly IMicOwnership _mic;
    private readonly ISpeechArbiter _arbiter;
    private readonly IOptionsMonitor<GsxOptions> _options;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<GateChoiceDialogue> _logger;
    private int _running;

    public GateChoiceDialogue(
        GateChoiceStore store,
        IMicOwnership mic,
        ISpeechArbiter arbiter,
        IOptionsMonitor<GsxOptions> options,
        JsonlEventLog eventLog,
        ILogger<GateChoiceDialogue> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(mic);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _mic = mic;
        _arbiter = arbiter;
        _options = options;
        _eventLog = eventLog;
        _logger = logger;
    }

    public void Start() => _store.QuestionRaised += OnQuestionRaised;

    public void Dispose() => _store.QuestionRaised -= OnQuestionRaised;

    private void OnQuestionRaised(object? sender, GateChoiceQuestion question) => _ = RunAsync(question);

    /// <summary>"36A" → "36 Alpha" — how the FO says a suffixed stand; other labels as they are.</summary>
    public static string SpokenLabel(string label)
    {
        ArgumentNullException.ThrowIfNull(label);
        var match = LabelShape.Match(label.Trim());
        if (!match.Success)
        {
            return label;
        }

        var letters = match.Groups["suffix"].Value.ToUpperInvariant()
            .Select(c => Nato.TryGetValue(c, out var word) ? char.ToUpperInvariant(word[0]) + word[1..] : c.ToString());
        return $"{match.Groups["number"].Value} {string.Join(" ", letters)}";
    }

    /// <summary>"W36 is 36 Alpha or 36 Bravo in this scenery — which?" Public for tests.</summary>
    public static string SpokenPrompt(GateChoiceQuestion question)
    {
        ArgumentNullException.ThrowIfNull(question);
        var spoken = question.Candidates.Select(c => SpokenLabel(c.Label)).ToList();
        var list = spoken.Count <= 1
            ? string.Join("", spoken)
            : $"{string.Join(", ", spoken.Take(spoken.Count - 1))} or {spoken[^1]}";
        return $"{question.Gate} is {list} in this scenery — which?";
    }

    /// <summary>
    /// Which candidate the pilot named: the label ("36A", "36 alpha"), the suffix alone
    /// ("alpha", a lone "a"), or an ordinal ("the first", "second"). Null when nothing or more
    /// than one candidate matches — the FO asks again rather than guessing.
    /// </summary>
    public static GateCandidate? Interpret(string heard, IReadOnlyList<GateCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(heard);
        ArgumentNullException.ThrowIfNull(candidates);
        var text = CommandMatcher.Normalize(heard);
        if (text.Length == 0)
        {
            return null;
        }

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var matches = new List<GateCandidate>();
        foreach (var candidate in candidates)
        {
            var label = CommandMatcher.Normalize(candidate.Label);
            var spoken = CommandMatcher.Normalize(SpokenLabel(candidate.Label));
            if (ContainsPhrase(text, label) || ContainsPhrase(text, spoken) || text.Replace(" ", "", StringComparison.Ordinal) == label.Replace(" ", "", StringComparison.Ordinal))
            {
                matches.Add(candidate);
                continue;
            }

            var shape = LabelShape.Match(candidate.Label.Trim());
            if (!shape.Success)
            {
                continue;
            }

            var suffix = shape.Groups["suffix"].Value.ToUpperInvariant();
            var natoWords = suffix.Select(c => Nato.TryGetValue(c, out var w) ? w : c.ToString().ToLowerInvariant()).ToList();
            if (natoWords.All(words.Contains) || (words.Length == 1 && words[0].Equals(suffix, StringComparison.OrdinalIgnoreCase)))
            {
                matches.Add(candidate);
            }
        }

        if (matches.Count == 0)
        {
            var ordinal = text switch
            {
                _ when ContainsPhrase(text, "first") || text == "one" || text == "1" => 0,
                _ when ContainsPhrase(text, "second") || text == "two" || text == "2" => 1,
                _ when ContainsPhrase(text, "third") || text == "three" || text == "3" => 2,
                _ => -1,
            };
            if (ordinal >= 0 && ordinal < candidates.Count)
            {
                return candidates[ordinal];
            }
        }

        return matches.Count == 1 ? matches[0] : null;
    }

    private static bool ContainsPhrase(string text, string phrase)
        => phrase.Length > 0 && Regex.IsMatch(text, $@"(?<![a-z0-9]){Regex.Escape(phrase)}(?![a-z0-9])");

    /// <summary>Runs one question; public so tests can drive it directly.</summary>
    public async Task RunAsync(GateChoiceQuestion question, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(question);
        if (!_options.CurrentValue.VoiceControlEnabled)
        {
            return;
        }

        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return;
        }

        try
        {
            IDisposable scope;
            try
            {
                scope = _mic.Borrow("gateChoice");
            }
            catch (InvalidOperationException)
            {
                _logger.LogDebug("Gate choice question skipped — microphone already borrowed");
                return;
            }

            using (scope)
            {
                var prompt = SpokenPrompt(question);
                var grammar = question.Candidates
                    .SelectMany(c => new[] { c.Label, SpokenLabel(c.Label) })
                    .Concat(["first", "the first", "second", "the second", "third"])
                    .Concat(ConfirmVocabulary.SayAgain)
                    .Concat(HandBack)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                _eventLog.Record("gate-choice-question", new { prompt, gate = question.Gate, options = question.Candidates.Select(c => c.Label).ToList() });
                await _arbiter.SpeakAsync(prompt, SpeechPriority.High, cancellationToken).ConfigureAwait(false);

                for (var attempt = 0; attempt < MaxAttempts; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_store.Snapshot().Question is null)
                    {
                        return; // answered elsewhere (web button, GSX settled it)
                    }

                    var heard = await _mic.ListenAsync(grammar, ListenTimeout, cancellationToken).ConfigureAwait(false);
                    if (heard is null)
                    {
                        continue;
                    }

                    var text = CommandMatcher.Normalize(heard);
                    if (HandBack.Any(h => text.Contains(h, StringComparison.Ordinal)))
                    {
                        break;
                    }

                    if (ConfirmVocabulary.SayAgain.Any(s => text.Contains(s, StringComparison.Ordinal)))
                    {
                        await _arbiter.SpeakAsync(prompt, SpeechPriority.High, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    var candidate = Interpret(heard, question.Candidates);
                    if (candidate is null)
                    {
                        await _arbiter.SpeakAsync($"Say again — {SpokenList(question)}?", SpeechPriority.High, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    if (_store.Choose(candidate.Label, "voice"))
                    {
                        _eventLog.Record("gate-choice-question", new { outcome = "answered", gate = question.Gate, chosen = candidate.Label, heard });
                        await _arbiter.SpeakAsync($"{SpokenLabel(candidate.Label)} it is.", SpeechPriority.High, cancellationToken).ConfigureAwait(false);
                    }

                    return;
                }

                var fallback = SpokenLabel(question.Candidates[0].Label);
                _eventLog.Record("gate-choice-question", new { outcome = "unanswered", gate = question.Gate, fallback = question.Candidates[0].Label });
                await _arbiter.SpeakAsync($"Your call — I'll take {fallback} at taxi-in unless you say otherwise.", SpeechPriority.Normal, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gate choice dialogue failed");
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private static string SpokenList(GateChoiceQuestion question)
    {
        var spoken = question.Candidates.Select(c => SpokenLabel(c.Label)).ToList();
        return spoken.Count <= 1 ? string.Join("", spoken) : $"{string.Join(", ", spoken.Take(spoken.Count - 1))} or {spoken[^1]}";
    }
}
