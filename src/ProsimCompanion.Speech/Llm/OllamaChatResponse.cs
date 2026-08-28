using System.Text;
using System.Text.Json;

namespace ProsimCompanion.Speech.Llm;

/// <summary>
/// Pure parser for Ollama's native <c>/api/chat</c> reply. Non-streaming is one JSON object;
/// streaming is newline-delimited JSON (one object per line, NOT SSE) ending with a line whose
/// <c>done</c> is true. Both are accepted regardless of what was asked for, so a server that
/// ignores <c>stream:false</c> still round-trips. Only <c>message.content</c> is ever returned —
/// a thinking-capable model puts its reasoning in a SEPARATE <c>message.thinking</c> field
/// (when thinking is enabled), and that must never reach the TTS text.
/// </summary>
public static class OllamaChatResponse
{
    /// <summary>Extracts the assistant text from a non-streaming or NDJSON body. Returns null
    /// when no chunk carried any content. Throws <see cref="JsonException"/> on malformed input.</summary>
    public static string? ExtractContent(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        var text = new StringBuilder();
        var any = false;
        foreach (var rawLine in body.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            using var doc = JsonDocument.Parse(line);
            if (TryReadContent(doc.RootElement, out var chunk))
            {
                any = true;
                text.Append(chunk);
            }
        }

        return any ? text.ToString() : null;
    }

    private static bool TryReadContent(JsonElement root, out string content)
    {
        content = "";
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("message", out var message)
            || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("content", out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        content = value.GetString() ?? "";
        return true;
    }
}
