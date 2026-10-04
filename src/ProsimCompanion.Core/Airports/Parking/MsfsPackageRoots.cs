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
