using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ProsimCompanion.Core.Localization;

/// <summary>
/// The UI language packs on disk and the rules that pick one (ADR-0014). Packs are
/// <c>{code}.json</c> files in the given directories; an earlier directory shadows a later
/// one by code, so the seeded user copies under <c>%LOCALAPPDATA%</c> win over the shipped
/// defaults beside the exe, and a pilot can drop in <c>pt-BR.json</c> without a build.
/// The directories are re-scanned at most every <see cref="RescanAfter"/> on access, so an
/// edited pack shows on the next render without a restart; an unparseable file logs and is
/// skipped, never fatal. English is the fallback for every text, so a half-translated pack
/// still renders a complete UI.
/// </summary>
public sealed class LanguagePackCatalog
{
    /// <summary>The pack every other pack falls back to, and the last-resort language.</summary>
    public const string DefaultCode = "en";

    private readonly IReadOnlyList<string> _directories;
    private readonly ILogger<LanguagePackCatalog> _logger;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, byte> _reportedMissing = new(StringComparer.Ordinal);

    private DateTime _lastScanUtc = DateTime.MinValue;
    private bool _loaded;
    private Dictionary<string, DateTime> _signature = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, LanguagePack> _byCode = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<LanguagePack> _ordered = [];

    /// <param name="directories">Pack folders, highest priority first.</param>
    /// <param name="logger">Diagnostics sink.</param>
    /// <param name="rescanAfter">Minimum time between directory scans; null keeps the 2 s
    /// default (a render reads strings many times, the file system once).</param>
    public LanguagePackCatalog(IReadOnlyList<string> directories, ILogger<LanguagePackCatalog> logger, TimeSpan? rescanAfter = null)
    {
        ArgumentNullException.ThrowIfNull(directories);
        ArgumentNullException.ThrowIfNull(logger);
        _directories = directories;
        _logger = logger;
        RescanAfter = rescanAfter ?? TimeSpan.FromSeconds(2);
    }

    /// <summary>How long a scan result is trusted before the folders are checked again.</summary>
    public TimeSpan RescanAfter { get; }

    /// <summary>Every readable pack: English first, then the rest by native name.</summary>
    public IReadOnlyList<LanguagePack> Packs
    {
        get
        {
            EnsureFresh();
            return _ordered;
        }
    }

    /// <summary>Forces the next access to re-read the folders (the Appearance page on open).</summary>
    public void Refresh()
    {
        lock (_gate)
        {
            _lastScanUtc = DateTime.MinValue;
        }
    }

    /// <summary>The pack for a language tag: exact code first, then its primary subtag
    /// (<c>de-AT</c> → <c>de</c>). Null for an empty or unknown tag.</summary>
    public LanguagePack? Find(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        EnsureFresh();
        var trimmed = code.Trim();
        if (_byCode.TryGetValue(trimmed, out var exact))
        {
            return exact;
        }

        var dash = trimmed.IndexOf('-');
        return dash > 0 && _byCode.TryGetValue(trimmed[..dash], out var primary) ? primary : null;
    }

    /// <summary>
    /// Picks the language to render: the configured code when a pack exists for it, else the
    /// first browser language with a pack, else English, else whatever pack exists. Never
    /// null — the UI always renders in some language.
    /// </summary>
    public string Resolve(string? configured, IReadOnlyList<string> browserLanguages)
    {
        ArgumentNullException.ThrowIfNull(browserLanguages);
        if (Find(configured) is { } chosen)
        {
            return chosen.Code;
        }

        foreach (var tag in browserLanguages)
        {
            if (Find(tag) is { } match)
            {
                return match.Code;
            }
        }

        if (Find(DefaultCode) is { } english)
        {
            return english.Code;
        }

        var packs = Packs;
        return packs.Count > 0 ? packs[0].Code : DefaultCode;
    }

    /// <summary>
    /// The text for <paramref name="textId"/> in <paramref name="languageCode"/>, falling back
    /// to English, then to the id itself. A missing id is logged once per language at
    /// Debug — enough to find untranslated strings, quiet enough for a half-done pack.
    /// </summary>
    public string Lookup(string languageCode, string textId, out bool found)
    {
        ArgumentNullException.ThrowIfNull(textId);
        found = true;
        if (Find(languageCode) is { } pack && pack.Strings.TryGetValue(textId, out var text))
        {
            return text;
        }

        if (!string.Equals(languageCode, DefaultCode, StringComparison.OrdinalIgnoreCase)
            && _reportedMissing.TryAdd(languageCode + ":" + textId, 0))
        {
            _logger.LogDebug("Language pack {Language} has no text for {TextId} — English shown", languageCode, textId);
        }

        if (Find(DefaultCode) is { } english && english.Strings.TryGetValue(textId, out var fallback))
        {
            return fallback;
        }

        found = false;
        if (_reportedMissing.TryAdd(DefaultCode + ":" + textId, 0))
        {
            _logger.LogWarning("No language pack has a text for {TextId} — the id itself is shown", textId);
        }

        return textId;
    }

    private void EnsureFresh()
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if (now - _lastScanUtc < RescanAfter)
            {
                return;
            }

            _lastScanUtc = now;
            var files = DiscoverFiles();
            if (SameSignature(files))
            {
                return;
            }

            Reload(files);
        }
    }

    /// <summary>Code → file, the first directory holding a code wins.</summary>
    private Dictionary<string, string> DiscoverFiles()
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in _directories)
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            IEnumerable<string> candidates;
            try
            {
                candidates = Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Language pack folder {Directory} unreadable — skipped", directory);
                continue;
            }

            foreach (var file in candidates)
            {
                var code = Path.GetFileNameWithoutExtension(file);
                if (code.Length > 0 && !code.StartsWith('.') && !files.ContainsKey(code))
                {
                    files[code] = file;
                }
            }
        }

        return files;
    }

    private bool SameSignature(Dictionary<string, string> files)
    {
        var signature = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, file) in files)
        {
            try
            {
                signature[file] = File.GetLastWriteTimeUtc(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                signature[file] = DateTime.MinValue;
            }
        }

        var same = _loaded
            && signature.Count == _signature.Count
            && signature.All(pair => _signature.TryGetValue(pair.Key, out var seen) && seen == pair.Value);
        _signature = signature;
        return same;
    }

    private void Reload(Dictionary<string, string> files)
    {
        var byCode = new Dictionary<string, LanguagePack>(StringComparer.OrdinalIgnoreCase);
        foreach (var (code, file) in files)
        {
            try
            {
                var pack = LanguagePack.Load(file);
                byCode[pack.Code] = pack;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Language pack {File} skipped — not a valid pack ({Code})", file, code);
            }
        }

        _loaded = true;
        _byCode = byCode;
        _ordered = byCode.Values
            .OrderBy(pack => pack.Code.Equals(DefaultCode, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(pack => pack.NativeName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _logger.LogInformation("Language packs loaded: {Codes}", string.Join(", ", _ordered.Select(p => p.Code)));
    }
}
