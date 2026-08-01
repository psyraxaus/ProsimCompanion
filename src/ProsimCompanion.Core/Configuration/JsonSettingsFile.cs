using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// Read/modify/write access to the user settings file (config/settings.json beside the exe).
/// Reads for binding go through <c>IOptionsMonitor&lt;T&gt;</c> (the configuration system watches the
/// same file with reloadOnChange); this type exists for the write side — the web UI mutates
/// individual sections and unrelated content is preserved, so partial files and hand edits survive.
/// </summary>
public sealed class JsonSettingsFile
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly object _gate = new();

    public JsonSettingsFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    /// <summary>Path of the underlying settings file.</summary>
    public string Path => _path;

    /// <summary>Returns the current settings document, or an empty object if the file is absent.</summary>
    public JsonObject Read()
    {
        lock (_gate)
        {
            return ReadCore();
        }
    }

    /// <summary>
    /// Applies <paramref name="mutate"/> to the settings document and writes it back atomically
    /// (temp file + move) so a crash mid-write can never truncate the user's settings.
    /// </summary>
    public void Update(Action<JsonObject> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        lock (_gate)
        {
            var root = ReadCore();
            mutate(root);

            var directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempPath = _path + ".tmp";
            File.WriteAllText(tempPath, root.ToJsonString(WriteOptions));
            File.Move(tempPath, _path, overwrite: true);
        }
    }

    /// <summary>Returns the named object section, creating it when absent.</summary>
    public static JsonObject GetOrCreateSection(JsonObject root, string name)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (root[name] is JsonObject existing)
        {
            return existing;
        }

        var section = new JsonObject();
        root[name] = section;
        return section;
    }

    private JsonObject ReadCore()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        var text = File.ReadAllText(_path);
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return JsonNode.Parse(text) as JsonObject ?? [];
    }
}
