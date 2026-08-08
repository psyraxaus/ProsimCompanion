using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Briefings;

/// <summary>Parses a spoken minima statement ("decision altitude three two zero",
/// "DH 200", "minimums six five zero feet") into the crew-entered minima model, and
/// recognizes the explicit "not briefed" refusal. Pure and deterministic.</summary>
public static class MinimaParser
{
    private static readonly string[] NotBriefedPhrases =
        ["not briefed", "no minimums", "no minima", "standby"];

    /// <summary>Phrases the capture grammar should accept beside the digit words.</summary>
    public static IReadOnlyList<string> GrammarPhrases { get; } =
    [
        "decision altitude", "decision height", "minimum descent altitude", "minimums",
        "minima", "feet", .. NotBriefedPhrases,
    ];

    public static bool IsNotBriefed(string utterance)
        => NotBriefedPhrases.Any(p => utterance.Contains(p, StringComparison.OrdinalIgnoreCase));

    public static ArrivalMinima? TryParse(string utterance)
    {
        if (string.IsNullOrWhiteSpace(utterance)
            || !NumberExtractor.TryExtract(utterance, out var value)
            || value is <= 0 or > 20_000)
        {
            return null;
        }

        var kind = Kind(utterance);
        return new ArrivalMinima(kind, Math.Round(value));
    }

    private static ArrivalMinimumKind Kind(string utterance)
    {
        bool Has(params string[] words)
            => words.Any(w => utterance.Contains(w, StringComparison.OrdinalIgnoreCase));

        if (Has("decision height", "d h", "dh", "radio"))
        {
            return ArrivalMinimumKind.DecisionHeight;
        }

        if (Has("minimum descent", "m d a", "mda"))
        {
            return ArrivalMinimumKind.MinimumDescentAltitude;
        }

        return ArrivalMinimumKind.DecisionAltitude;
    }
}

/// <summary>
/// The interactive minima sub-dialogue of the arrival brief (Prosim2FO parity): prompt →
/// capture → read back → confirm, over the exclusive-mic seam so normal routing (and a
/// running checklist) holds untouched. Never proceed past minima on an unconfirmed value —
/// confirmed or "not briefed" only; retries per settings, then the briefing speaks
/// "Minimums not briefed.". A confirmed value lands in the shared
/// <see cref="ArrivalMinimaStore"/>, arming the minimums callouts exactly like the web entry.
/// Free-form capture works best with the LAN transcription engine; the closed-grammar
/// offline engine falls back to the digit-word grammar and may time out to "not briefed".
/// </summary>
public sealed class MinimaCaptureDialogue
{
    private static readonly IReadOnlyList<string> CaptureGrammar =
        [.. NumberExtractor.GrammarWords, .. MinimaParser.GrammarPhrases];

    private readonly IOptionsMonitor<BriefingOptions> _options;
    private readonly IMicOwnership _mic;
    private readonly ArrivalMinimaStore _minima;
    private readonly ISpeechArbiter _arbiter;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<MinimaCaptureDialogue> _logger;

    public MinimaCaptureDialogue(
        IOptionsMonitor<BriefingOptions> options,
        IMicOwnership mic,
        ArrivalMinimaStore minima,
        ISpeechArbiter arbiter,
        JsonlEventLog eventLog,
        ILogger<MinimaCaptureDialogue> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(mic);
        ArgumentNullException.ThrowIfNull(minima);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _mic = mic;
        _minima = minima;
        _arbiter = arbiter;
        _eventLog = eventLog;
        _logger = logger;
    }

    /// <summary>Runs the sub-dialogue and returns the confirmed minima (also stored), the
    /// pre-existing crew entry, or null for "not briefed". Skips silently when capture is
    /// disabled or the mic is already borrowed by another dialogue.</summary>
    public async Task<ArrivalMinima?> RunAsync(CancellationToken cancellationToken)
    {
        // A crew entry from the web page (or an earlier capture) already counts as briefed.
        if (_minima.Current is { } existing)
        {
            return existing;
        }

        var options = _options.CurrentValue;
        if (!options.MinimaCaptureEnabled)
        {
            return null;
        }

        IDisposable scope;
        try
        {
            scope = _mic.Borrow("minimaCapture");
        }
        catch (InvalidOperationException)
        {
            _logger.LogDebug("Minima capture skipped — microphone already borrowed");
            return null;
        }

        using (scope)
        {
            var timeout = TimeSpan.FromSeconds(Math.Max(5, options.MinimaListenTimeoutSeconds));
            await _arbiter.SpeakAsync("Confirm the approach minimums.", SpeechPriority.Normal, cancellationToken)
                .ConfigureAwait(false);

            for (var attempt = 0; attempt <= options.MinimaMaxRetries; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var heard = await _mic.ListenAsync(CaptureGrammar, timeout, cancellationToken)
                    .ConfigureAwait(false);
                if (heard is null)
                {
                    continue; // timeout consumes an attempt
                }

                if (MinimaParser.IsNotBriefed(heard))
                {
                    _eventLog.Record("minima.notBriefed", new { heard });
                    return null;
                }

                var parsed = MinimaParser.TryParse(heard);
                if (parsed is null)
                {
                    await _arbiter.SpeakAsync("Say again the minimums.", SpeechPriority.Normal, cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                await _arbiter.SpeakAsync(
                    $"{Capitalize(BriefingComposer.MinimaCallout(parsed))}, confirm?", SpeechPriority.Normal, cancellationToken)
                    .ConfigureAwait(false);
                var answer = await _mic.ListenAsync(ConfirmVocabulary.All, timeout, cancellationToken)
                    .ConfigureAwait(false);
                if (answer is not null
                    && ConfirmVocabulary.Affirm.Any(a => answer.Contains(a, StringComparison.OrdinalIgnoreCase)))
                {
                    _minima.Set(parsed);
                    _eventLog.Record("minima.captured", new { kind = parsed.Kind.ToString(), altitudeFt = parsed.AltitudeFt });
                    return parsed;
                }

                await _arbiter.SpeakAsync("Say the minimums again.", SpeechPriority.Normal, cancellationToken)
                    .ConfigureAwait(false);
            }

            _eventLog.Record("minima.notBriefed", new { heard = (string?)null });
            return null;
        }
    }

    private static string Capitalize(string text)
        => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
