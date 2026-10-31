using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Core.Persona;

/// <summary>
/// Persistence for <c>fo-roster-state.json</c> (issue #194) so the drawn First Officer
/// survives an app restart mid-day. Same contract as <see cref="Day.DayStateFile"/>: atomic
/// temp-then-move writes, an empty file is "no draw", a corrupt file is moved aside as
/// <c>.corrupt-&lt;timestamp&gt;.bak</c> and the roster starts clean, never a throw. Lives
/// beside <c>daystate.json</c> under the app's data root, NOT in <c>settings.json</c>: a
/// settings page saves its whole section, and a draw made after the page loaded would be
/// written back stale by the next Save.
/// </summary>
public sealed class FoRosterStateFile
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly ILogger<FoRosterStateFile> _logger;
    private readonly object _io = new();

    /// <param name="path">The file; null = <c>%LOCALAPPDATA%\ProsimCompanion\fo-roster-state.json</c>.</param>
    public FoRosterStateFile(ILogger<FoRosterStateFile> logger, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _path = string.IsNullOrWhiteSpace(path) ? Path.Combine(UserDataPaths.Root, "fo-roster-state.json") : path;
    }

    /// <summary>Where the state lives (the Roster card names it).</summary>
    public string FilePath => _path;

    /// <summary>The persisted state, or an empty one when the file is absent, empty or corrupt.</summary>
    public FoRosterState Load()
    {
        try
        {
            lock (_io)
            {
                if (!File.Exists(_path))
                {
                    return new FoRosterState();
                }

                var json = File.ReadAllText(_path);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return new FoRosterState();
                }

                return JsonSerializer.Deserialize<FoRosterState>(json, JsonOptions) ?? new FoRosterState();
            }
        }
        catch (Exception ex)
        {
            var backup = _path + $".corrupt-{DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.bak";
            try
            {
                File.Move(_path, backup, overwrite: true);
            }
            catch (Exception moveEx)
            {
                _logger.LogDebug(moveEx, "Could not move the corrupt FO roster state aside");
            }

            _logger.LogWarning(ex, "FO roster state corrupt — backed up to {Backup} and starting without a draw", backup);
            return new FoRosterState();
        }
    }

    /// <summary>Writes the state atomically. Never throws — a failed save costs restart
    /// recovery, not the running day.</summary>
    public void Save(FoRosterState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var tmp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            lock (_io)
            {
                var json = JsonSerializer.Serialize(state, JsonOptions);
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(tmp, json);
                File.Move(tmp, _path, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not write the FO roster state {Path}", _path);
            try
            {
                if (File.Exists(tmp))
                {
                    File.Delete(tmp);
                }
            }
            catch (Exception deleteEx)
            {
                _logger.LogDebug(deleteEx, "Could not delete {Path}", tmp);
            }
        }
    }
}
