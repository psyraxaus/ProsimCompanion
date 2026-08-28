using System.Text.Json;
using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Speech.Recognition;

/// <summary>One parsed utterance from either LAN ASR flavour, normalised to what the
/// interpreter's confidence ladder consumes.</summary>
/// <param name="Text">Trimmed transcript; empty when the server heard silence.</param>
/// <param name="Confidence">0..1 geometric-mean token probability (exp of the duration-weighted
/// <c>avg_logprob</c>), or null when the server offered nothing usable.</param>
/// <param name="NoSpeechProb">Highest per-segment <c>no_speech_prob</c>, or null when absent.</param>
public sealed record AsrTranscript(string Text, double? Confidence, double? NoSpeechProb);

/// <summary>
/// Pure request/response knowledge for the two LAN ASR server shapes
/// (<see cref="AsrApiKind"/>), kept out of the recognizer so the parsing is unit-testable
/// without a microphone. The faster-whisper wrapper pre-computes confidence server-side;
/// whisper.cpp only returns per-segment <c>avg_logprob</c>/<c>no_speech_prob</c> in
/// <c>verbose_json</c>, so the same duration-weighted formula the wrapper uses
/// (Prosim2FO <c>deploy/faster-whisper/server.py</c>) is applied here — the interpreter's
/// thresholds (<c>confidenceFloor</c>, <c>noSpeechCeiling</c>) then mean the same thing for
/// both servers.
/// </summary>
public static class AsrServerApi
{
    /// <summary>Path the utterance WAV is POSTed to, relative to <c>speech.asrBaseUrl</c>.</summary>
    public static string TranscribePath(AsrApiKind api) => api switch
    {
        AsrApiKind.WhisperCpp => "/v1/audio/transcriptions",
        _ => "/transcribe",
    };

    /// <summary>Multipart field that carries the grammar phrases for decoder biasing —
    /// faster-whisper's <c>hotwords</c>, whisper.cpp's <c>prompt</c> (its initial prompt).</summary>
    public static string BiasField(AsrApiKind api) => api switch
    {
        AsrApiKind.WhisperCpp => "prompt",
        _ => "hotwords",
    };

    /// <summary>Extra form fields the flavour needs beyond <c>file</c> and the bias field.
    /// whisper.cpp defaults to plain <c>json</c> (text only) — <c>verbose_json</c> is what
    /// carries the segment statistics; temperature 0 keeps short commands deterministic.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> ExtraFields(AsrApiKind api) => api switch
    {
        AsrApiKind.WhisperCpp =>
        [
            new("response_format", "verbose_json"),
            new("temperature", "0"),
        ],
        _ => [],
    };

    /// <summary>Whether a readiness-probe status means "server is up". whisper.cpp builds
    /// without a <c>/health</c> route answer 404 while perfectly able to transcribe, so for
    /// that flavour any HTTP answer at all counts.</summary>
    public static bool IsReady(AsrApiKind api, int statusCode)
        => statusCode is >= 200 and < 300 || (api == AsrApiKind.WhisperCpp && statusCode == 404);

    /// <summary>Parses a transcribe response body. Returns null when the body is not the
    /// expected JSON shape (the caller treats that as a dropped utterance and logs it).</summary>
    public static AsrTranscript? Parse(AsrApiKind api, string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var text = root.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String
                ? textElement.GetString()?.Trim() ?? ""
                : "";

            return api == AsrApiKind.WhisperCpp
                ? ParseWhisperCpp(root, text)
                : ParseFasterWhisper(root, text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static AsrTranscript ParseFasterWhisper(JsonElement root, string text)
        => new(text, ReadNumber(root, "confidence"), ReadNumber(root, "no_speech_prob"));

    private static AsrTranscript ParseWhisperCpp(JsonElement root, string text)
    {
        if (!root.TryGetProperty("segments", out var segments) || segments.ValueKind != JsonValueKind.Array)
        {
            // Plain "json" response_format (text only): nothing to grade on, let the
            // interpreter fall back to its text-only ladder.
            return new AsrTranscript(text, null, null);
        }

        double span = 0;
        double weightedLogprob = 0;
        double? noSpeech = null;
        var any = false;
        foreach (var segment in segments.EnumerateArray())
        {
            any = true;
            var start = ReadNumber(segment, "start") ?? 0;
            var end = ReadNumber(segment, "end") ?? start;
            var duration = Math.Max(0, end - start);
            if (ReadNumber(segment, "avg_logprob") is { } logprob)
            {
                span += duration;
                weightedLogprob += logprob * duration;
            }

            if (ReadNumber(segment, "no_speech_prob") is { } segmentNoSpeech)
            {
                noSpeech = noSpeech is null ? segmentNoSpeech : Math.Max(noSpeech.Value, segmentNoSpeech);
            }
        }

        if (!any)
        {
            // Server-side VAD swallowed everything — same verdict the wrapper returns.
            return new AsrTranscript("", 0.0, 1.0);
        }

        double? confidence = span > 0 ? Math.Exp(weightedLogprob / span) : null;
        return new AsrTranscript(text, confidence, noSpeech);
    }

    private static double? ReadNumber(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;
}
