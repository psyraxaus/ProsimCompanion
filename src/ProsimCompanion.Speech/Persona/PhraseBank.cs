using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ProsimCompanion.Speech.Persona;

/// <summary>
/// Acknowledgement phrase pools, file-backed by <c>config/phrases.json</c> (Prosim2FO format:
/// a flat object of string arrays, keys <c>areYouSure</c> and <c>didNotCatch</c>). A missing
/// or unparseable file falls back to the shipped defaults; edits are picked up on the next
/// access via a last-write-time check (no watcher thread). Round-robin via <c>Next*</c> — the
/// persona layer draws RANDOM picks from the same lists, fixing the predecessor wart where
/// enabling persona silently shadowed the user's phrases.json with hardcoded pools.
/// </summary>
public sealed class PhraseBank
{
    private static readonly string[] DefaultAreYouSure =
        ["Are you sure?", "Confirm that?", "Double-check that one?", "Say again — that doesn't look set."];

    private static readonly string[] DefaultDidNotCatch = ["Say again?", "Didn't catch that.", "Repeat please."];

    private readonly string _path;
    private readonly ILogger<PhraseBank> _logger;
    private readonly object _gate = new();
    private string[] _areYouSure = DefaultAreYouSure;
    private string[] _didNotCatch = DefaultDidNotCatch;
    private DateTime _loadedWriteTimeUtc = DateTime.MinValue;
    private int _areYouSureIndex;
    private int _didNotCatchIndex;

    public PhraseBank(string configDirectory, ILogger<PhraseBank> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);
        ArgumentNullException.ThrowIfNull(logger);
        _path = Path.Combine(configDirectory, "phrases.json");
        _logger = logger;
    }

    public string NextAreYouSure()
    {
        var list = AreYouSure;
        return list[(int)((uint)Interlocked.Increment(ref _areYouSureIndex) % (uint)list.Count)];
    }

    public string NextDidNotCatch()
    {
        var list = DidNotCatch;
        return list[(int)((uint)Interlocked.Increment(ref _didNotCatchIndex) % (uint)list.Count)];
    }

    /// <summary>Current pool (persona random picks read these — same file, same lists).</summary>
    public IReadOnlyList<string> AreYouSure
    {
        get
        {
            ReloadIfChanged();
            return _areYouSure;
        }
    }

    public IReadOnlyList<string> DidNotCatch
    {
        get
        {
            ReloadIfChanged();
            return _didNotCatch;
        }
    }

    private void ReloadIfChanged()
    {
        lock (_gate)
        {
            DateTime writeTime;
            try
            {
                writeTime = File.GetLastWriteTimeUtc(_path); // MinValue-ish when absent
            }
            catch (IOException)
            {
                return;
            }

            if (writeTime == _loadedWriteTimeUtc)
            {
                return;
            }
            _loadedWriteTimeUtc = writeTime;

            if (!File.Exists(_path))
            {
                _areYouSure = DefaultAreYouSure;
                _didNotCatch = DefaultDidNotCatch;
                return;
            }

            try
            {
                using var document = JsonDocument.Parse(
                    File.ReadAllText(_path),
                    new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                _areYouSure = ReadPool(document, "areYouSure", DefaultAreYouSure);
                _didNotCatch = ReadPool(document, "didNotCatch", DefaultDidNotCatch);
                _logger.LogInformation(
                    "phrases.json loaded ({AreYouSure} are-you-sure, {DidNotCatch} did-not-catch phrases)",
                    _areYouSure.Length,
                    _didNotCatch.Length);
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                _logger.LogWarning(ex, "phrases.json unreadable — using the shipped defaults");
                _areYouSure = DefaultAreYouSure;
                _didNotCatch = DefaultDidNotCatch;
            }
        }
    }

    private static string[] ReadPool(JsonDocument document, string key, string[] defaults)
    {
        if (!document.RootElement.TryGetProperty(key, out var element)
            || element.ValueKind != JsonValueKind.Array)
        {
            return defaults;
        }

        var phrases = element.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToArray();
        return phrases.Length > 0 ? phrases : defaults;
    }
}
