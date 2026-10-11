using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Core.Flight;

/// <summary>
/// One feature's share of the flight-state snapshot (issue #167). A part serialises what it
/// would lose in an app restart and takes it back at startup; the
/// <see cref="FlightStateSnapshotService"/> owns the file, the identity check and the write
/// cadence. Parts never see each other — the restore order is the only coupling, and it is
/// declared, not discovered.
/// </summary>
public interface IFlightStatePart
{
    /// <summary>The JSON key under <c>parts</c>. Stable across builds: a renamed key is a
    /// snapshot nobody restores.</summary>
    string Key { get; }

    /// <summary>Restore order, lowest first. Latches that must be set before a store
    /// publishes (the notification once-per-cycle set before the flight-times store fires its
    /// "landed" edge) take a lower number than the stores they guard.</summary>
    int RestoreOrder => 100;

    /// <summary>Raised after the part's state changed; the service debounces the write.</summary>
    event Action? Changed;

    /// <summary>The state to persist, or null when there is nothing worth keeping.</summary>
    JsonNode? Capture();

    /// <summary>Takes the persisted state back. May throw — the service logs and skips the
    /// part; the others still restore.</summary>
    void Restore(JsonNode node);
}

/// <summary>What identifies the flight the snapshot belongs to — compared with the aircraft
/// and the EFB at startup before anything is restored.</summary>
public sealed record FlightStateIdentity(
    string? OfpRequestId,
    string? OriginIcao,
    string? DestinationIcao,
    string? FlightNumber,
    string? AircraftTitle);

/// <summary>The on-disk document: <c>%LOCALAPPDATA%\ProsimCompanion\state\flight-state.json</c>.</summary>
public sealed record FlightStateSnapshotFile(
    int Version,
    DateTimeOffset WrittenUtc,
    string? SessionId,
    FlightStateIdentity Identity,
    Dictionary<string, JsonNode?> Parts)
{
    public const int CurrentVersion = 1;
}

/// <summary>What the startup check decided about the file.</summary>
public enum FlightStateRestoreVerdict
{
    /// <summary>The evidence (aircraft title, EFB flag, FMS route) has not arrived yet —
    /// ask again when a value changes.</summary>
    Wait,

    /// <summary>The snapshot belongs to this flight: restore it.</summary>
    Restore,

    /// <summary>The snapshot is stale or belongs to another flight/aircraft: delete it.</summary>
    Discard,
}

/// <summary>
/// The pure startup rule (issue #167): a snapshot is restored only when it is young enough
/// AND the aircraft is the same AND the EFB still reports the SimBrief plan imported (or the
/// MCDU carries the snapshot's route). Every input is nullable so the rule can say "not yet"
/// while ProSim is still delivering values — a missing value is never read as a mismatch.
/// </summary>
public static class FlightStateSnapshotRules
{
    public static (FlightStateRestoreVerdict Verdict, string Reason) Validate(
        FlightStateSnapshotFile snapshot,
        DateTimeOffset nowUtc,
        TimeSpan maxAge,
        bool? planImported,
        string? fmsOrigin,
        string? fmsDestination,
        string? aircraftTitle)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.Version != FlightStateSnapshotFile.CurrentVersion)
        {
            return (FlightStateRestoreVerdict.Discard, $"snapshot version {snapshot.Version} is not {FlightStateSnapshotFile.CurrentVersion}");
        }

        var age = nowUtc - snapshot.WrittenUtc;
        if (age > maxAge || age < TimeSpan.FromHours(-1))
        {
            return (FlightStateRestoreVerdict.Discard, $"snapshot is {age.TotalHours:F1} h old (limit {maxAge.TotalHours:F0} h)");
        }

        var identity = snapshot.Identity;
        if (!string.IsNullOrWhiteSpace(identity.AircraftTitle))
        {
            if (aircraftTitle is null)
            {
                return (FlightStateRestoreVerdict.Wait, "aircraft title not received yet");
            }

            if (!string.Equals(aircraftTitle.Trim(), identity.AircraftTitle.Trim(), StringComparison.Ordinal))
            {
                return (FlightStateRestoreVerdict.Discard, $"aircraft is \"{aircraftTitle}\", snapshot was \"{identity.AircraftTitle}\"");
            }
        }

        // A snapshot without an OFP (a leg flown on an MCDU plan alone) has no plan identity
        // to check: the title and the age are the whole fence.
        if (string.IsNullOrWhiteSpace(identity.OfpRequestId))
        {
            return (FlightStateRestoreVerdict.Restore, "same aircraft, no OFP identity to compare");
        }

        if (planImported == true)
        {
            return (FlightStateRestoreVerdict.Restore, "EFB still reports the SimBrief plan imported");
        }

        var fmsMatches = FmsPlanRules.IsValidIcao(fmsOrigin) && FmsPlanRules.IsValidIcao(fmsDestination)
            && string.Equals(fmsOrigin, identity.OriginIcao, StringComparison.OrdinalIgnoreCase)
            && string.Equals(fmsDestination, identity.DestinationIcao, StringComparison.OrdinalIgnoreCase);
        if (fmsMatches)
        {
            return (FlightStateRestoreVerdict.Restore, $"MCDU route {fmsOrigin}-{fmsDestination} matches the snapshot");
        }

        if (planImported is null || fmsOrigin is null || fmsDestination is null)
        {
            return (FlightStateRestoreVerdict.Wait, "EFB plan flag / MCDU route not received yet");
        }

        return (FlightStateRestoreVerdict.Discard,
            $"EFB reports no plan imported and the MCDU route ({fmsOrigin}-{fmsDestination}) is not the snapshot's ({identity.OriginIcao}-{identity.DestinationIcao})");
    }
}

/// <summary>
/// The file behind the snapshot: atomic temp-then-move writes, a corrupt file moved aside
/// as <c>.corrupt-&lt;timestamp&gt;.bak</c>, never a throw — the same contract as
/// <see cref="Gate.ArrivalGateStateFile"/> and the day state.
/// </summary>
public sealed class FlightStateSnapshotStore
{
    /// <summary>Shared by every part that serialises a record: camelCase, enums as strings,
    /// nulls dropped, so the file reads like the rest of the app's JSON.</summary>
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly ILogger<FlightStateSnapshotStore> _logger;
    private readonly string _path;

    public FlightStateSnapshotStore(ILogger<FlightStateSnapshotStore> logger)
        : this(logger, System.IO.Path.Combine(UserDataPaths.Root, "state", "flight-state.json"))
    {
    }

    /// <summary>Test seam: an explicit file path.</summary>
    public FlightStateSnapshotStore(ILogger<FlightStateSnapshotStore> logger, string path)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _logger = logger;
        _path = path;
    }

    public string Path => _path;

    /// <summary>The persisted snapshot, or null when absent or unreadable (a corrupt file is
    /// backed up beside itself and forgotten).</summary>
    public FlightStateSnapshotFile? Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            return JsonSerializer.Deserialize<FlightStateSnapshotFile>(File.ReadAllText(_path), JsonOptions);
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
                _logger.LogDebug(moveEx, "Could not move the corrupt flight-state snapshot aside");
            }

            _logger.LogWarning(ex, "Flight-state snapshot corrupt — backed up to {Backup} and starting without it", backup);
            return null;
        }
    }

    /// <summary>Writes atomically. Never throws — a failed save costs restart recovery only.</summary>
    public bool Save(FlightStateSnapshotFile snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, JsonOptions));
            File.Move(tmp, _path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not write the flight-state snapshot {Path}", _path);
            return false;
        }
    }

    /// <summary>Removes the file (the leg is over, or the snapshot belongs to another flight).
    /// Never throws.</summary>
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
            _logger.LogDebug(ex, "Could not delete the flight-state snapshot {Path}", _path);
        }
    }
}
