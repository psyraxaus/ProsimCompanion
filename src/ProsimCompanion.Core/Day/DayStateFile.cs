using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Core.Day;

/// <summary>
/// Persistence for <c>daystate.json</c> so an open day survives an app restart mid-rotation.
/// Same robustness contract as the logbook/tech-log stores: options-overridable path (the
/// predecessor hard-coded <c>%LOCALAPPDATA%</c>), atomic temp-then-move writes, a corrupt file
/// moved aside as <c>.corrupt-&lt;timestamp&gt;.bak</c> (the unified naming — the predecessor
/// used a one-off <c>.bad-*</c> scheme here) and rebuilt, never a throw.
/// </summary>
public sealed class DayStateFile
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly IOptionsMonitor<DayOptions> _options;
    private readonly ILogger<DayStateFile> _logger;
    private readonly object _io = new();

    public DayStateFile(IOptionsMonitor<DayOptions> options, ILogger<DayStateFile> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _logger = logger;
    }

    /// <summary>Loads the persisted day state; null when absent. A corrupt file is backed up
    /// beside itself and null is returned — the day simply starts clean. An EMPTY file is
    /// "no day", not corruption: on the owner's sim PC every start from 2026-10-03 to
    /// 2026-10-31 found a 0-byte daystate.json (nine corrupt-*.bak files, all empty, each
    /// stamped about a minute after the previous run's last log line), so the day never
    /// survived a restart and the log carried a stack trace for it. The empty file is removed
    /// and the fact logged plainly; which writer empties it is still open (issue filed
    /// 2026-10-31) — Save now logs every write with its size to find out.</summary>
    public DayState? Load()
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
                    _logger.LogInformation("Day state file {Path} is empty — starting without a day (no backup taken)", path);
                    TryDelete(path);
                    return null;
                }

                return JsonSerializer.Deserialize<DayState>(json, JsonOptions);
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
                // Best effort — a locked/permission-denied file just stays put; starting
                // fresh matters more than the backup.
                _logger.LogDebug(moveEx, "Could not move corrupt day state aside");
            }

            _logger.LogWarning(ex, "Day state corrupt — backed up to {Backup} and starting fresh", backup);
            return null;
        }
    }

    /// <summary>Writes the day state atomically (temp-then-move). Never throws — a failed save
    /// costs restart recovery, not the running day. Writes are serialized and each uses its
    /// own temp name, so two callers (the phase handler and the leg-facts step both save) can
    /// never truncate each other's temp file; nothing but a complete document is ever moved
    /// into place.</summary>
    public void Save(DayState day)
    {
        ArgumentNullException.ThrowIfNull(day);
        var path = ResolvePath();
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            lock (_io)
            {
                var json = JsonSerializer.Serialize(day, JsonOptions);
                if (string.IsNullOrWhiteSpace(json))
                {
                    _logger.LogWarning("Day state serialized to nothing — not written to {Path}", path);
                    return;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(tmp, json);
                File.Move(tmp, path, overwrite: true);
                _logger.LogInformation("Day state saved ({Bytes} bytes, day {DayId}, {State})", json.Length, day.DayId, day.State);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not write day state {Path}", path);
            TryDelete(tmp);
        }
    }

    private void TryDelete(string path)
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

    private string ResolvePath()
    {
        var configured = _options.CurrentValue.Path;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ProsimCompanion",
            "daystate.json");
    }
}
