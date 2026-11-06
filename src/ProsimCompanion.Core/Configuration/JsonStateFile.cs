using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// The one "state file" shape (review 2026-11-05: <c>fo-roster-state.json</c>,
/// <c>scenario-state.json</c> and <c>daystate.json</c> each re-typed it): a JSON document under
/// the app's data root, read under a lock, a corrupt file moved aside as
/// <c>.corrupt-&lt;timestamp&gt;.bak</c> and read as absent, written atomically (a fresh temp
/// name per write, then a move — two writers can never truncate each other, and nothing but
/// a complete document is ever moved into place). Never a throw out of <see cref="LoadOrNull"/>
/// or <see cref="Write"/>: a failed save costs restart recovery, not the running flight.
/// Derived files add their own policy through the three hooks.
/// </summary>
/// <typeparam name="T">The document type (a plain settable class).</typeparam>
public abstract class JsonStateFile<T>
    where T : class
{
    private readonly ILogger _logger;
    private readonly string _what;
    private readonly JsonSerializerOptions _json;
    private readonly object _io = new();

    /// <param name="what">The file's name in log lines ("Day state", "Scenario state").</param>
    protected JsonStateFile(ILogger logger, string what, JsonSerializerOptions json)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(what);
        ArgumentNullException.ThrowIfNull(json);
        _logger = logger;
        _what = what;
        _json = json;
    }

    /// <summary>Where the file lives right now (a derived file may resolve it from options).</summary>
    public string FilePath => ResolvePath();

    /// <summary>The file's path at the moment of the call.</summary>
    protected abstract string ResolvePath();

    /// <summary>The document, or null when the file is absent, empty (see <see cref="OnEmpty"/>)
    /// or corrupt (backed up beside itself, logged at Warning).</summary>
    protected T? LoadOrNull()
    {
        var path = ResolvePath();
        try
        {
            lock (_io)
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                var json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json))
                {
                    OnEmpty(path);
                    return null;
                }

                return JsonSerializer.Deserialize<T>(json, _json);
            }
        }
        catch (Exception ex)
        {
            var backup = path + $".corrupt-{DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.bak";
            try
            {
                File.Move(path, backup, overwrite: true);
            }
            catch (Exception moveEx)
            {
                // Best effort — a locked / permission-denied file stays put; starting fresh
                // matters more than the backup.
                _logger.LogDebug(moveEx, "Could not move the corrupt {What} aside", _what);
            }

            _logger.LogWarning(ex, "{What} corrupt — backed up to {Backup}; starting fresh", _what, backup);
            return null;
        }
    }

    /// <summary>Writes the document atomically. Never throws.</summary>
    protected void Write(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var path = ResolvePath();
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            lock (_io)
            {
                var json = JsonSerializer.Serialize(value, _json);
                if (!AcceptJson(path, json))
                {
                    return;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(tmp, json);
                File.Move(tmp, path, overwrite: true);
                OnSaved(path, json, value);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not write the {What} {Path}", _what, path);
            TryDelete(tmp);
        }
    }

    /// <summary>Removes the file (the state is "nothing"). Never throws.</summary>
    protected void Delete()
    {
        var path = ResolvePath();
        try
        {
            lock (_io)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete the {What} {Path}", _what, path);
        }
    }

    /// <summary>An empty (0-byte or whitespace) file was found — read as "nothing". The default
    /// is silent; a derived file may log and remove it.</summary>
    protected virtual void OnEmpty(string path)
    {
    }

    /// <summary>Whether the serialised document may be written; false skips the write.</summary>
    protected virtual bool AcceptJson(string path, string json) => true;

    /// <summary>After a successful write (under the lock) — a derived file may log it.</summary>
    protected virtual void OnSaved(string path, string json, T value)
    {
    }

    /// <summary>Deletes a file if it exists, logging a failure at Debug.</summary>
    protected void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not delete {Path}", path);
        }
    }
}
