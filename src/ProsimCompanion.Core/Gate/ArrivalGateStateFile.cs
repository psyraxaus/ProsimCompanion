using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Core.Gate;

/// <summary>The persisted arrival-gate queue: what was confirmed, for which destination,
/// whether it already fired, and when. camelCase JSON in <c>arrival-gate.json</c>.</summary>
public sealed record ArrivalGateState(
    string Gate,
    string? DestinationIcao,
    bool Fired,
    DateTimeOffset SavedAtUtc);

/// <summary>
/// Persistence for the queued arrival gate so an app restart in flight does not lose it.
/// 2026-09-20 LIRF→EGLL: gate 545R was confirmed twice, the app restarted twice in the
/// descent (installing betas), and the last run landed with no gate at all — GSX then asked
/// "Select Position" on the landing roll and the SayIntentions request went out with a blank
/// stand. Same robustness contract as <see cref="Day.DayStateFile"/>: atomic temp-then-move
/// writes, a corrupt file moved aside and forgotten, never a throw.
/// </summary>
public sealed class ArrivalGateStateFile
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ILogger<ArrivalGateStateFile> _logger;
    private readonly string _path;

    public ArrivalGateStateFile(ILogger<ArrivalGateStateFile> logger)
        : this(logger, Path.Combine(UserDataPaths.Root, "arrival-gate.json"))
    {
    }

    /// <summary>Test seam: an explicit file path.</summary>
    public ArrivalGateStateFile(ILogger<ArrivalGateStateFile> logger, string path)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _logger = logger;
        _path = path;
    }

    /// <summary>The persisted state, or null when absent or unreadable (a corrupt file is
    /// backed up beside itself — the queue simply starts empty).</summary>
    public ArrivalGateState? Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            return JsonSerializer.Deserialize<ArrivalGateState>(File.ReadAllText(_path), JsonOptions);
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
                _logger.LogDebug(moveEx, "Could not move corrupt arrival-gate state aside");
            }

            _logger.LogWarning(ex, "Arrival-gate state corrupt — backed up to {Backup} and starting empty", backup);
            return null;
        }
    }

    /// <summary>Writes atomically. Never throws — a failed save costs restart recovery only.</summary>
    public void Save(ArrivalGateState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state, JsonOptions));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not write arrival-gate state {Path}", _path);
        }
    }

    /// <summary>Removes the file (queue cancelled or the leg is over). Never throws.</summary>
    public void Clear()
    {
        try
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not delete arrival-gate state {Path}", _path);
        }
    }
}
