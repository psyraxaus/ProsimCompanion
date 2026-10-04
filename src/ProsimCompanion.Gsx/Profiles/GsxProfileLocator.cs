using ProsimCompanion.Core.Airports.Parking;

namespace ProsimCompanion.Gsx.Profiles;

/// <summary>A profile file found for an airport, with where it came from.</summary>
/// <param name="Path">Full path.</param>
/// <param name="Tier">"user" (GSX's own folder — highest priority, manual p.68) or "package"
/// (shipped inside a scenery package, read-only "designer-provided").</param>
public sealed record GsxProfileFile(string Path, string Tier)
{
    public string FileName => System.IO.Path.GetFileName(Path);
    public bool IsIni => Path.EndsWith(".ini", StringComparison.OrdinalIgnoreCase);
    public bool IsPy => Path.EndsWith(".py", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Finds GSX airport profiles for an ICAO. Two places (manual p.68, "Customization files
/// Priority"): the user folder <c>%APPDATA%\Virtuali\GSX\MSFS</c> (an <c>.ini</c> and/or a
/// <c>.py</c> named <c>&lt;icao&gt;-…</c>), and an <c>.ini</c> a scenery developer put inside
/// the package itself (e.g. <c>Community\inibuilds-airport-egll-heathrow\Official GSX
/// Profile\egll-24-iniBuilds.ini</c> — EGLL has NO user ini, only that one). GSX's built-in
/// profiles are encrypted <c>.pye</c> and cannot be read. Pure file-system lookups; every
/// failure is swallowed into "not found".
/// </summary>
public static class GsxProfileLocator
{
    private static readonly string[] PackageSubRoots = ["Community", @"Official\OneStore", @"Official\Steam"];

    /// <summary>Profiles for the airport, user tier first, then package inis; newest first
    /// within a tier so a re-saved profile shadows an older copy of the same airport.</summary>
    public static IReadOnlyList<GsxProfileFile> Find(string icao, string? userFolder, IEnumerable<string> packageRoots)
    {
        ArgumentNullException.ThrowIfNull(packageRoots);
        var id = (icao ?? "").Trim();
        if (id.Length < 3)
        {
            return [];
        }

        var found = new List<GsxProfileFile>();
        var user = string.IsNullOrWhiteSpace(userFolder) ? MsfsPackageRoots.GsxUserProfileFolder : userFolder.Trim().Trim('"');
        foreach (var file in EnumerateNamed(user, id, ["*.ini", "*.py"], maxDepth: 1))
        {
            found.Add(new GsxProfileFile(file, "user"));
        }

        foreach (var root in packageRoots.Where(SafeDirectoryExists))
        {
            foreach (var sub in PackageSubRoots)
            {
                var subRoot = Path.Combine(root, sub);
                if (!SafeDirectoryExists(subRoot))
                {
                    continue;
                }

                foreach (var package in SafeEnumerateDirectories(subRoot))
                {
                    // A package named for the airport is searched deeply; any other package only
                    // two levels down (where "Official GSX Profile\x.ini" style folders sit).
                    var named = Path.GetFileName(package).Contains(id, StringComparison.OrdinalIgnoreCase);
                    foreach (var file in EnumerateNamed(package, id, ["*.ini"], maxDepth: named ? 5 : 2))
                    {
                        found.Add(new GsxProfileFile(file, "package"));
                    }
                }
            }
        }

        return found
            .DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f.Tier == "user" ? 0 : 1)
            .ThenByDescending(f => SafeLastWrite(f.Path))
            .ToList();
    }

    /// <summary>Whether a file name belongs to the airport: starts with the ICAO and is followed
    /// by a separator or the extension ("EFHK-MKStudios.py", "egll-24-iniBuilds.ini", "LGAV.ini"),
    /// so "EFHKX…" or "KEFHK" never match.</summary>
    public static bool NameMatches(string fileName, string icao)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (!stem.StartsWith(icao, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return stem.Length == icao.Length || stem[icao.Length] is '-' or '_' or '.' or ' ';
    }

    private static IEnumerable<string> EnumerateNamed(string folder, string icao, string[] patterns, int maxDepth)
    {
        if (!SafeDirectoryExists(folder))
        {
            yield break;
        }

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = maxDepth > 0,
            MaxRecursionDepth = Math.Max(0, maxDepth),
            MatchCasing = MatchCasing.CaseInsensitive,
        };

        foreach (var pattern in patterns)
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(folder, pattern, options).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                continue;
            }

            foreach (var file in files)
            {
                if (NameMatches(Path.GetFileName(file), icao))
                {
                    yield return file;
                }
            }
        }
    }

    private static List<string> SafeEnumerateDirectories(string folder)
    {
        try
        {
            return Directory.EnumerateDirectories(folder).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool SafeDirectoryExists(string? path) => MsfsPackageRoots.SafeDirectoryExists(path);

    private static DateTime SafeLastWrite(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }
}
