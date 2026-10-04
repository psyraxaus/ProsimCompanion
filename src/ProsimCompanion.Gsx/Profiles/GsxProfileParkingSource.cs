using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Airports.Parking;
using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Gsx.Profiles;

/// <summary>
/// The GSX-profile tier of the parking catalogue: every <c>.ini</c> stand (position, radii,
/// wingspan, jetway, operators, pushback) merged under GSX's priority rule — user folder over
/// package copy — and every <c>.py</c>-listed stand, each carrying the name GSX will print
/// when the template is a literal. Reads files on every call (the catalogue caches); a
/// missing folder, an unreadable file or a file that is not a profile all degrade to "nothing".
/// </summary>
public sealed class GsxProfileParkingSource : IAirportParkingSource
{
    private readonly IOptionsMonitor<GsxOptions> _options;
    private readonly ILogger<GsxProfileParkingSource> _logger;

    public GsxProfileParkingSource(IOptionsMonitor<GsxOptions> options, ILogger<GsxProfileParkingSource> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _logger = logger;
    }

    public string Name => "gsx-profile";

    public int Order => 10;

    public Task<AirportParkingSourceResult?> LoadAsync(string icao, CancellationToken cancellationToken = default)
    {
        var options = _options.CurrentValue;
        if (!options.ResolveGatesFromProfiles)
        {
            return Task.FromResult<AirportParkingSourceResult?>(null);
        }

        return Task.Run(() => Load(icao, options), cancellationToken);
    }

    /// <summary>The package roots in force: configured ones, else the auto-detected ones.</summary>
    public static IReadOnlyList<string> PackageRoots(GsxOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var configured = options.SceneryPackageFolders
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => f.Trim().Trim('"'))
            .ToList();
        return configured.Count > 0 ? configured : MsfsPackageRoots.Detect();
    }

    private AirportParkingSourceResult? Load(string icao, GsxOptions options)
    {
        var files = GsxProfileLocator.Find(icao, options.GsxProfileFolder, PackageRoots(options));
        if (files.Count == 0)
        {
            return null;
        }

        var stands = new Dictionary<ParkingIdentity, AirportParking>();
        var notes = new List<string>();
        GsxPyProfile? py = null;

        foreach (var file in files)
        {
            string text;
            try
            {
                text = File.ReadAllText(file.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "GSX profile {File} could not be read", file.Path);
                continue;
            }

            if (file.IsIni)
            {
                var ini = GsxIniProfileParser.Parse(text);
                if (ini.Stands.Count == 0)
                {
                    continue; // not a GSX airport profile (or an empty one)
                }

                var added = 0;
                foreach (var stand in ini.Stands.Where(s => s.MarsId is null))
                {
                    var parking = GsxIniProfileParser.ToParking(stand);
                    if (stands.TryGetValue(parking.Identity, out var existing))
                    {
                        // Earlier files rank higher (user tier, then newest): keep their values.
                        stands[parking.Identity] = existing.MergeWith(parking);
                    }
                    else
                    {
                        stands[parking.Identity] = parking;
                        added++;
                    }
                }

                notes.Add($"{file.FileName} ({file.Tier}, {ini.Stands.Count} stands{(added < ini.Stands.Count ? $", {added} new" : "")})");
            }
            else if (file.IsPy && py is null)
            {
                var parsed = GsxPyProfileParser.Parse(text);
                if (parsed.Groups.Count == 0)
                {
                    continue; // a handler script or a stop-position-only file
                }

                py = parsed;
                notes.Add($"{file.FileName} ({file.Tier}, {parsed.Groups.Count} name groups)");
            }
        }

        if (py is not null)
        {
            // Stands the .py lists that no ini customised — GSX still names them.
            foreach (var identity in py.ListedStands())
            {
                if (!stands.ContainsKey(identity))
                {
                    stands[identity] = AirportParking.Bare(identity, ParkingDataSources.GsxPy);
                }
            }

            foreach (var (identity, parking) in stands.ToList())
            {
                if (py.Resolve(identity) is { } name)
                {
                    stands[identity] = parking with
                    {
                        GsxUiName = name.UiName,
                        GsxGateName = name.GateName,
                        Sources = parking.Sources | ParkingDataSources.GsxPy,
                    };
                }
            }
        }

        if (stands.Count == 0)
        {
            return null;
        }

        _logger.LogDebug("GSX profile for {Icao}: {Count} stands from {Notes}", icao, stands.Count, string.Join(", ", notes));
        return new AirportParkingSourceResult(stands.Values.ToList(), string.Join(", ", notes));
    }
}
