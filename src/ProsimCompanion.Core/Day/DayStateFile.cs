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

    public DayStateFile(IOptionsMonitor<DayOptions> options, ILogger<DayStateFile> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _logger = logger;
    }

    /// <summary>Loads the persisted day state; null when absent. A corrupt file is backed up
    /// beside itself and null is returned — the day simply starts clean.</summary>
    public DayState? Load()
    {
        var path = ResolvePath();
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            return JsonSerializer.Deserialize<DayState>(File.ReadAllText(path), JsonOptions);
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
    /// costs restart recovery, not the running day.</summary>
    public void Save(DayState day)
    {
        ArgumentNullException.ThrowIfNull(day);
        var path = ResolvePath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(day, JsonOptions));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not write day state {Path}", path);
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
