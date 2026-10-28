using System.Text.RegularExpressions;

namespace ProsimCompanion.Core.Airports.Parking;

/// <summary>
/// Where MSFS keeps its packages (the folders holding <c>Community</c> and <c>Official</c>)
/// and where GSX keeps its user airport profiles, detected from this machine. Shared by the
/// GSX profile reader and the settings page (which shows the pilot what will be scanned).
/// Pure file-system probes; every failure reads as "not there".
/// </summary>
public static class MsfsPackageRoots
{
    /// <summary>GSX's user profile folder: <c>%APPDATA%\Virtuali\GSX\MSFS</c> (manual p.68).</summary>
    public static string GsxUserProfileFolder
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Virtuali", "GSX", "MSFS");

    /// <summary>
    /// Package roots that exist on this machine: the Store/Xbox-app LocalCache locations for
    /// MSFS 2020 (<c>Microsoft.FlightSimulator_…</c>) and 2024 (<c>Microsoft.Limitless_…</c>),
    /// the Steam locations under <c>%APPDATA%</c>, and whatever each sim's <c>UserCfg.opt</c>
    /// <c>InstalledPackagesPath</c> points at (packages moved to another drive).
    /// </summary>
    public static IReadOnlyList<string> Detect()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var candidates = new List<string>();
        var userCfgs = new List<string>();

        foreach (var store in new[] { "Microsoft.FlightSimulator_8wekyb3d8bbwe", "Microsoft.Limitless_8wekyb3d8bbwe" })
        {
            var cache = Path.Combine(local, "Packages", store, "LocalCache");
            candidates.Add(Path.Combine(cache, "Packages"));
            userCfgs.Add(Path.Combine(cache, "UserCfg.opt"));
        }

        foreach (var steam in new[] { "Microsoft Flight Simulator", "Microsoft Flight Simulator 2024" })
        {
            var folder = Path.Combine(roaming, steam);
            candidates.Add(Path.Combine(folder, "Packages"));
            userCfgs.Add(Path.Combine(folder, "UserCfg.opt"));
        }

        foreach (var cfg in userCfgs)
        {
            if (ReadInstalledPackagesPath(cfg) is { } configured)
            {
                candidates.Add(configured);
            }
        }

        return candidates
            .Where(SafeDirectoryExists)
            .Select(p => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The <c>InstalledPackagesPath "D:\MSFS\Packages"</c> line of a UserCfg.opt, or null.</summary>
    public static string? ReadInstalledPackagesPath(string userCfgPath)
    {
        try
        {
            if (!File.Exists(userCfgPath))
            {
                return null;
            }

            foreach (var line in File.ReadLines(userCfgPath))
            {
                var match = Regex.Match(line, @"^\s*InstalledPackagesPath\s+""(?<path>[^""]+)""", RegexOptions.CultureInvariant);
                if (match.Success)
                {
                    return match.Groups["path"].Value;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable config: nothing to add.
        }

        return null;
    }

    /// <summary>
    /// The MSFS 2024 <c>LocalCache</c> folders that exist on this machine — where the sim writes
    /// a pilot's saved custom cameras (<c>SimObjects\Airplanes\&lt;aircraft&gt;\…\cameras.cfg</c>,
    /// the Display &amp; FOV page). Store: <c>%LOCALAPPDATA%\Packages\Microsoft.Limitless_…\LocalCache</c>;
    /// Steam: <c>%APPDATA%\Microsoft Flight Simulator 2024\LocalCache</c> (per the design doc,
    /// unverified on a Steam install). MSFS 2020 is deliberately not listed: the feature
    /// targets 2024 only. A sibling of <see cref="Detect"/>, which keeps answering with the
    /// package roots.
    /// </summary>
    public static IReadOnlyList<string> DetectLocalCache2024()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return new[]
            {
                Path.Combine(local, "Packages", "Microsoft.Limitless_8wekyb3d8bbwe", "LocalCache"),
                Path.Combine(roaming, "Microsoft Flight Simulator 2024", "LocalCache"),
            }
            .Where(SafeDirectoryExists)
            .Select(p => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// True when Parallel 42's ChasePlane for MSFS 2024 has a work folder on this machine
    /// (<c>…\LocalState\WASM\MSFS2024\p42-util-chaseplane\work</c> for the Store build,
    /// <c>%APPDATA%\Microsoft Flight Simulator 2024\WASM\MSFS2024\p42-util-chaseplane\work</c>
    /// for Steam). Detection only: its preset format is undocumented and is never written.
    /// </summary>
    public static bool ChasePlaneDetected()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return SafeDirectoryExists(Path.Combine(local, "Packages", "Microsoft.Limitless_8wekyb3d8bbwe", "LocalState", "WASM", "MSFS2024", "p42-util-chaseplane", "work"))
               || SafeDirectoryExists(Path.Combine(roaming, "Microsoft Flight Simulator 2024", "WASM", "MSFS2024", "p42-util-chaseplane", "work"));
    }

    public static bool SafeDirectoryExists(string? path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}
