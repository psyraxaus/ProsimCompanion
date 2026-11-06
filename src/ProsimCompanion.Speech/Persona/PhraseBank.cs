using System.Text.Json;
using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Speech.Persona;

/// <summary>
/// Acknowledgement phrase pools, file-backed by <c>config/phrases.json</c> (Prosim2FO format:
/// a flat object of string arrays, keys <c>areYouSure</c> and <c>didNotCatch</c>) on the
/// <see cref="UserConfigFileBank{T}"/> contract. A missing or unparseable file falls back to
/// the shipped defaults; edits are picked up on the next access via a last-write-time check
/// (no watcher thread). Round-robin via <c>Next*</c> — the persona layer draws RANDOM picks
/// from the same lists, fixing the predecessor wart where enabling persona silently shadowed
/// the user's phrases.json with hardcoded pools.
/// </summary>
public sealed class PhraseBank : UserConfigFileBank<PhraseBank.Pools>
{
    /// <summary>The two pools as loaded.</summary>
    public sealed record Pools(string[] AreYouSure, string[] DidNotCatch);

    private static readonly string[] DefaultAreYouSure =
        ["Are you sure?", "Confirm that?", "Double-check that one?", "Say again — that doesn't look set."];

    private static readonly string[] DefaultDidNotCatch = ["Say again?", "Didn't catch that.", "Repeat please."];

    private static readonly Pools Defaults = new(DefaultAreYouSure, DefaultDidNotCatch);

    private readonly ILogger<PhraseBank> _logger;
    private int _areYouSureIndex;
    private int _didNotCatchIndex;

    /// <summary>Optional <paramref name="problems"/>: an unparseable phrases.json surfaces on
    /// the web UI (issue #74) instead of only a log warning.</summary>
    public PhraseBank(string configDirectory, ILogger<PhraseBank> logger, Core.State.ConfigProblemStore? problems = null)
        : base(Path.Combine(configDirectory ?? throw new ArgumentNullException(nameof(configDirectory)), "phrases.json"), Core.State.ConfigAreas.Phrases, logger, problems, Defaults)
    {
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
    public IReadOnlyList<string> AreYouSure => Current.AreYouSure;

    public IReadOnlyList<string> DidNotCatch => Current.DidNotCatch;

    protected override (Pools Value, IReadOnlyList<string> Problems) Parse(string json)
    {
        using var document = JsonDocument.Parse(json, JsonConfigText.DocumentOptions);
        return (new Pools(ReadPool(document, "areYouSure", DefaultAreYouSure), ReadPool(document, "didNotCatch", DefaultDidNotCatch)), []);
    }

    /// <summary>An unreadable file yields the shipped defaults — never a stale user pool.</summary>
    protected override Pools OnUnreadable(Pools lastGood) => Defaults;

    protected override void OnLoaded(Pools value)
        => _logger.LogInformation(
            "phrases.json loaded ({AreYouSure} are-you-sure, {DidNotCatch} did-not-catch phrases)",
            value.AreYouSure.Length,
            value.DidNotCatch.Length);

    private static string[] ReadPool(JsonDocument document, string poolName, string[] defaults)
    {
        if (!document.RootElement.TryGetProperty(poolName, out var element)
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
