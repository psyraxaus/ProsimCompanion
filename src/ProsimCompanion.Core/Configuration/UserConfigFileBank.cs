using System.Text.Json;
using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// The one "user JSON bank" shape (review 2026-11-05: the walkaround findings, the scenario
/// catalogue, the FO roster, the phrase pools and the region facts each re-typed it): a file in
/// the USER config tree (ADR-0007), re-read on the next access after its last-write time moved
/// (no watcher thread), parse problems reported to the Config page through
/// <see cref="ConfigProblemStore"/> and logged once, an unreadable file keeping the last good
/// value (or a derived bank's default), an absent file reading as the empty value. Derived
/// banks supply the parse and their own log lines.
/// </summary>
/// <typeparam name="T">What the file yields (a list, a dictionary, a record of pools).</typeparam>
public abstract class UserConfigFileBank<T>
    where T : class
{
    private readonly ILogger _logger;
    private readonly ConfigProblemStore? _problems;
    private readonly string _area;
    private readonly T _empty;
    private readonly object _gate = new();
    private T _value;
    private DateTime _loadedWriteTimeUtc = DateTime.MinValue;

    /// <param name="path">The file's full path.</param>
    /// <param name="area">The <see cref="ConfigAreas"/> entry the problems are filed under.</param>
    /// <param name="empty">The value an absent file yields (and the starting value).</param>
    protected UserConfigFileBank(string path, string area, ILogger logger, ConfigProblemStore? problems, T empty)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(area);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(empty);
        FilePath = path;
        _area = area;
        _logger = logger;
        _problems = problems;
        _empty = empty;
        _value = empty;
    }

    /// <summary>Where the file lives.</summary>
    public string FilePath { get; }

    /// <summary>The file's name, for log lines.</summary>
    protected string FileName => Path.GetFileName(FilePath);

    /// <summary>The current value, re-read when the file changed since the last access.</summary>
    protected T Current
    {
        get
        {
            lock (_gate)
            {
                ReloadIfChanged();
                return _value;
            }
        }
    }

    /// <summary>Parses the file's text into the value and the problems to report (an empty list
    /// = clean). A <see cref="JsonException"/> out of here is "unreadable".</summary>
    protected abstract (T Value, IReadOnlyList<string> Problems) Parse(string json);

    /// <summary>The value to hold after an unreadable file: the last good one by default (a
    /// bank with shipped defaults returns those instead).</summary>
    protected virtual T OnUnreadable(T lastGood) => lastGood;

    /// <summary>Called when the file is absent (after the value became the empty one).</summary>
    protected virtual void OnAbsent()
    {
    }

    /// <summary>Called after a successful parse — the derived bank's "loaded" log line.</summary>
    protected virtual void OnLoaded(T value)
    {
    }

    private void ReloadIfChanged()
    {
        DateTime writeTime;
        try
        {
            writeTime = File.GetLastWriteTimeUtc(FilePath); // MinValue-ish when absent
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
        if (!File.Exists(FilePath))
        {
            _value = _empty;
            _problems?.ClearArea(_area);
            OnAbsent();
            return;
        }

        try
        {
            var (value, problems) = Parse(File.ReadAllText(FilePath));
            _value = value;
            if (problems.Count > 0)
            {
                _problems?.Report(_area, FilePath, string.Join("; ", problems));
                _logger.LogWarning("{File}: {Problems}", FileName, string.Join("; ", problems));
            }
            else
            {
                _problems?.ClearArea(_area);
            }

            OnLoaded(value);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogWarning(ex, "{File} unreadable — keeping the last good value", FileName);
            _problems?.Report(_area, FilePath, ex.Message);
            _value = OnUnreadable(_value);
        }
    }
}

/// <summary>The small JSON readers the user-config parsers share (review 2026-11-05: three
/// model parsers each had their own <c>Text</c> and <c>NullIfBlank</c>).</summary>
public static class JsonConfigText
{
    /// <summary>The string property <paramref name="name"/>, or null when absent or not a string.</summary>
    public static string? Text(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>Null for a blank value, otherwise the value trimmed.</summary>
    public static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>The document options every user config file is read with: comments and
    /// trailing commas allowed (hand-edited files).</summary>
    public static JsonDocumentOptions DocumentOptions { get; } = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
}
