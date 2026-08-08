using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Speech.Llm;

namespace ProsimCompanion.Speech.Persona;

/// <summary>
/// The persona restyle path for short deterministic lines (advisories today): ask the LLM to
/// rephrase in the persona's voice with every number locked (the shared
/// <see cref="NumberVerifier"/>, allowed set = the numbers in the deterministic text), one
/// strict re-ask, per-line cache, hard timeout — and the deterministic text as the floor on
/// every miss. Persona off returns the text untouched with no event (exact previous
/// behaviour); everything else logs a <c>styling.decision</c> event. Ladder ported from
/// Prosim2FO's StyledSpeechService.
/// </summary>
public sealed class StyledSpeechService
{
    private const int CacheCap = 300;

    private readonly PersonaService _persona;
    private readonly OpenAiChatClient _llm;
    private readonly IOptionsMonitor<PersonaOptions> _options;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<StyledSpeechService> _logger;
    private readonly object _cacheGate = new();
    private readonly Dictionary<string, string> _cache = [];

    public StyledSpeechService(
        PersonaService persona,
        IOptionsMonitor<PersonaOptions> options,
        IOptionsMonitor<BriefingOptions> briefingOptions,
        JsonlEventLog eventLog,
        ILogger<StyledSpeechService> logger,
        OpenAiChatClient? llm = null)
    {
        ArgumentNullException.ThrowIfNull(persona);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(briefingOptions);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _persona = persona;
        _options = options;
        _eventLog = eventLog;
        _logger = logger;
        _llm = llm ?? new OpenAiChatClient(briefingOptions);
    }

    /// <summary>Returns the styled line, or the deterministic text on any miss. Never throws.</summary>
    public async Task<string> StyleAsync(
        string deterministicText,
        PersonaStyleCategory category,
        string cacheKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deterministicText))
        {
            return deterministicText;
        }

        // Persona off: exact previous behaviour, no event logged (deliberate).
        if (!_persona.Enabled)
        {
            return deterministicText;
        }

        if (!_persona.StylingEnabled(category))
        {
            Record(category, cacheKey, "skipped", 0, "category-off");
            return deterministicText;
        }

        var key = $"{category}:{cacheKey}";
        lock (_cacheGate)
        {
            if (_cache.TryGetValue(key, out var cached))
            {
                Record(category, cacheKey, "cached", 0, null);
                return cached;
            }
        }

        if (!_llm.IsConfigured)
        {
            // Cache the deterministic text so a disabled LLM is not re-evaluated per line.
            Cache(key, deterministicText);
            Record(category, cacheKey, "fallback", 0, "llm-off");
            return deterministicText;
        }

        var started = Environment.TickCount64;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(Math.Max(500, _options.CurrentValue.StyleTimeoutMs));

            var system = _persona.SystemPromptFragment(category) + Instruction(category);
            var user = "REPHRASE (keep all facts and numbers):\n" + deterministicText;
            var allowed = ExtractNumbers(deterministicText);

            var styled = Clean(await _llm.CompleteAsync(system, user, cts.Token).ConfigureAwait(false));
            if (!string.IsNullOrWhiteSpace(styled) && !NumberVerifier.Check(styled, allowed).Ok)
            {
                var stricter = user
                    + "\n\nUse ONLY these numbers, exactly as written, and no others: "
                    + NumberVerifier.DescribeAllowed(allowed);
                styled = Clean(await _llm.CompleteAsync(system, stricter, cts.Token).ConfigureAwait(false));
                if (!string.IsNullOrWhiteSpace(styled) && !NumberVerifier.Check(styled, allowed).Ok)
                {
                    styled = null;
                }
            }

            var latency = Environment.TickCount64 - started;
            if (string.IsNullOrWhiteSpace(styled))
            {
                Record(category, cacheKey, "fallback", latency, "unverified-or-empty");
                return deterministicText;
            }

            Cache(key, styled);
            Record(category, cacheKey, "styled", latency, null);
            return styled;
        }
        catch (Exception ex)
        {
            var latency = Environment.TickCount64 - started;
            _logger.LogDebug(ex, "Persona restyle failed — deterministic text speaks");
            Record(category, cacheKey, "fallback", latency, "error-or-timeout");
            return deterministicText;
        }
    }

    private static string Instruction(PersonaStyleCategory category)
    {
        var what = category switch
        {
            PersonaStyleCategory.Advisory => "this short flight-deck advisory",
            PersonaStyleCategory.Debrief => "this post-flight debrief",
            _ => "this line",
        };
        return $"Rephrase {what} in your own natural spoken voice. "
            + "Keep every number and fact identical and present; add nothing new. "
            + "Plain spoken English for text-to-speech: no markdown, no lists, no quotes. Return only the spoken line.";
    }

    /// <summary>Every numeric token of the deterministic text is the allowed set — the styled
    /// line may reuse them and nothing else.</summary>
    private static double[] ExtractNumbers(string text)
        => [.. Regex.Matches(text, @"\d+(?:\.\d+)?")
            .Select(match => double.Parse(match.Value, CultureInfo.InvariantCulture))];

    private static string? Clean(string? text)
        => text?.Trim().Trim('"', '\'', '`');

    private void Cache(string key, string value)
    {
        lock (_cacheGate)
        {
            if (_cache.Count >= CacheCap)
            {
                _cache.Clear(); // simple bound: advisory sets are small; a reset is harmless
            }
            _cache[key] = value;
        }
    }

    private void Record(PersonaStyleCategory category, string cacheKey, string outcome, long latencyMs, string? reason)
        => _eventLog.Record("styling.decision", new
        {
            category = category.ToString().ToLowerInvariant(),
            cacheKey,
            outcome,
            latencyMs,
            reason,
        });
}
