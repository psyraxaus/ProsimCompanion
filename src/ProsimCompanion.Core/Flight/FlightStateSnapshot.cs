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

    /// <summary>True for a part that belongs to the gap BETWEEN legs rather than to a flight
    /// (ADR-0019 §9, issue #220: the leg-boundary state). The service keeps writing such parts
    /// after the Shutdown finalisation and a ColdAndDark commit, when every flight part is
    /// suspended, as a boundary-only document; only a new leg, the end of duty or the age
    /// limit drops them.</summary>
    bool PersistsAcrossLegs => false;

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
/// AND the aircraft is the same AND the plan is the same — the plan the app already holds
/// (its request id), else the EFB's "SimBrief plan imported" flag NOT contradicted by the
/// MCDU route, else the MCDU route itself. Every input is nullable so the rule can say "not
/// yet" while ProSim is still delivering values — a missing value is never read as a mismatch.
/// <para>
/// ADR-0019 §9 (issue #220, audit A13): the EFB flag alone used to be enough, so a plan
/// imported in the EFB while the app was down (the flag is true for ANY imported plan) would
/// have restored the previous plan's snapshot. Now a request id the app already holds decides
/// outright, and the flag is overruled by a valid MCDU route that is not the snapshot's. The
/// residual — flag true, MCDU empty, no plan held — still restores: nothing contradicts it.
/// </para>
/// <para>
/// A <b>boundary-only</b> document (every part <see cref="IFlightStatePart.PersistsAcrossLegs"/>:
/// the leg-boundary state between two legs) belongs to the turnaround, not to a plan: it is
/// restored on the aircraft and the age alone — the FMS may be cleared, the EFB may still
/// hold the flown plan, a different plan held by the app means the leg already began and
/// the boundary is over (discard).
/// </para>
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
        string? aircraftTitle,
        string? currentRequestId = null,
        bool boundaryOnly = false)
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

        // The plan the app already holds is the strongest witness: the same id is the same
        // leg, a different id is the next one (or another flight altogether). Both sides go
        // through OfpPlanIdentity (the one spelling of "same plan", ADR-0019 §2); a snapshot
        // written with a bare request id still compares.
        if (!string.IsNullOrWhiteSpace(currentRequestId))
        {
            return string.Equals(Aircraft.Ofp.OfpPlanIdentity.Normalize(currentRequestId), Aircraft.Ofp.OfpPlanIdentity.Normalize(identity.OfpRequestId), StringComparison.Ordinal)
                ? (FlightStateRestoreVerdict.Restore, $"the app already holds the snapshot's plan (request {currentRequestId})")
                : (FlightStateRestoreVerdict.Discard,
                    boundaryOnly
                        ? $"a different plan (request {currentRequestId}) is already loaded — the leg began, the turnaround is over"
                        : $"the app holds request {currentRequestId}, the snapshot was {identity.OfpRequestId}");
        }

        if (boundaryOnly)
        {
            // Between two legs the FMS is cleared or still the flown pair and the EFB still
            // says "imported" for the flown plan: none of that identifies anything. Same
            // aircraft, young enough, no other plan held — the boundary stands.
            return (FlightStateRestoreVerdict.Restore, "leg-boundary state (between legs): same aircraft, no other plan held");
        }

        var fmsPairValid = FmsPlanRules.IsValidIcao(fmsOrigin) && FmsPlanRules.IsValidIcao(fmsDestination);
        var fmsMatches = fmsPairValid
            && string.Equals(fmsOrigin, identity.OriginIcao, StringComparison.OrdinalIgnoreCase)
            && string.Equals(fmsDestination, identity.DestinationIcao, StringComparison.OrdinalIgnoreCase);
        if (fmsMatches)
        {
            return (FlightStateRestoreVerdict.Restore, $"MCDU route {fmsOrigin}-{fmsDestination} matches the snapshot");
        }

        if (planImported == true)
        {
            // The flag is true for whatever plan the EFB holds — a route in the box that is
            // not the snapshot's says it is another one (ADR-0019 §9).
            return fmsPairValid
                ? (FlightStateRestoreVerdict.Discard,
                    $"EFB reports a plan imported but the MCDU route ({fmsOrigin}-{fmsDestination}) is not the snapshot's ({identity.OriginIcao}-{identity.DestinationIcao}) — another plan")
                : (FlightStateRestoreVerdict.Restore, "EFB still reports the SimBrief plan imported and the MCDU does not contradict it");
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
